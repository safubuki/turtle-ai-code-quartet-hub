using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public enum LaunchStageSlotState
{
    AlreadyRunning,
    Pending,
    Launching,
    Ready,
    Failed,
    Closing,
    Closed
}

public sealed record LaunchStageSlotEntry(
    string SlotName,
    int SlotIndex,
    string DisplayTitle,
    string ApplicationName,
    LaunchStageSlotState State,
    bool IsTarget);

public sealed record StagingVeilTarget(
    string SlotName,
    string DisplayTitle,
    string ApplicationName,
    WindowArranger.WindowBounds Bounds,
    LaunchStageSlotState InitialState,
    int MonitorIndex = 0);

// ワークスペースアプリの起動・終了時に表示する高貴・確実動作の演出オーバーレイ。
//
// 確実な透過フェードと滑らかさの両立:
//   - WPF は AllowsTransparency=False のウィンドウへ後から WS_EX_LAYERED を設定する操作を
//     WM_STYLECHANGING で自動的に剥ぎ取る仕様のため、象限ベール（QuadrantVeilWindow）・
//     黒い全画面シールド（MonitorBackdropWindow）・中央 HUD（CenterHudWindow）のすべてを
//     正式な AllowsTransparency=True とし、WPF レンダースレッド独立クロックの DoubleAnimation で駆動する。
//   - 重い BlurEffect / DropShadowEffect / BitmapCache は一切使わず、フリーズ済みの純グラデーションと
//     多重ソフトリングだけで「ほのかに光が漏れる LED のぼやけ」を軽快に描く。
//
// 重なり順（上から下へ）:
//   1. 中央 HUD（CenterHudWindow）
//   2. メインパネル本体（MainWindow）
//   3. 4 分割の象限ベール（QuadrantVeilWindow）
//   4. 黒い全画面シールド（MonitorBackdropWindow: 4 分割の十字隙間・角丸隙間・外周を含めて完全遮蔽）
//   5. 裏側の実ウィンドウ群（VS Code 等）
//
// 起動演出（Launch）:
//   - 起動・整列・安定化の間は、一括起動（複数スロット同時）では黒い全画面シールド＋象限ベールで
//     裏のチラつきを 1px も見せず、単体起動では対象スロットの象限ベール＋中央 HUD のみで軽やかに覆う。
//   - 全員の配置安定化が完了したら静かな一拍（一括 260ms／単体 120ms）の間に背面の黒い全画面シールドを
//     スッと引き（この間も象限ベールは 100% 不透明で各ウィンドウを覆っており裏は完全静止）、
//     BeginTime 一括タイムラインにより A→B→C→D へ 480ms 間隔で 1 つずつ LED ランプが灯って開く。
//
// 終了演出（Close）:
//   - 一括終了（複数スロット同時）では黒い全画面シールド＋象限ベールを一斉に被せ、十字隙間を含め裏の終了差を
//     完全遮断する。単体の閉じる・クリアでは対象スロットの象限ベール＋中央 HUD のみを表示する。
//   - 全員の終了が確認できたら、人間が「すべて完了した」と知覚できるワンクッション（一括 720ms／単体 460ms）を
//     置いたあと、黒い全画面シールド・全象限ベール・中央 HUD へ同じ瞬間に 420ms の軽やかな
//     フェードアウト（CubicEase EaseOut）を一斉投入して「フッ」と綺麗に消え失せさせる。
public sealed class LaunchStagingOverlay : IDisposable
{
    private static readonly TimeSpan LaunchCoverFadeInDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CloseCoverFadeInDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreWaveBatchHoldDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan PreWaveSingleHoldDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan BackdropPreRevealFadeOutDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan WaveStaggerDelay = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan WaveGlowLeadDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan WaveUncloakLeadDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WaveVeilFadeOutDuration = TimeSpan.FromMilliseconds(580);
    private static readonly TimeSpan HudFinalFadeOutDelayAfterLastGlow = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan HudFinalFadeOutDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan CloseDoneBatchHoldDuration = TimeSpan.FromMilliseconds(720);
    private static readonly TimeSpan CloseDoneSingleHoldDuration = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan CloseVanishFadeOutDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan DismissFadeOutDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HudFadeInDuration = TimeSpan.FromMilliseconds(360);

    // 終了完了後に人間が「すべて終わった」と知覚できるワンクッション時間を返す（テスト対象）。
    internal static TimeSpan GetCloseDoneHoldDuration(bool isBatch)
    {
        return isBatch ? CloseDoneBatchHoldDuration : CloseDoneSingleHoldDuration;
    }

    // 黒い全画面シールドを挟むべきかを判定する（テスト対象）。
    // 複数スロットを同時に扱う一括起動・一括終了（すべて閉じる）時のみ黒い全画面シールドを挟み、
    // 単体（1 スロット）の起動・終了・クリア時は他画面を覆わないよう象限ベール＋中央 HUD のみを表示する。
    internal static bool ShouldUseFullScreenBackdrop(bool isClosingMode, int veilTargetCount)
    {
        _ = isClosingMode;
        return veilTargetCount > 1;
    }

    private static readonly TimeSpan DefaultLaunchFailsafeTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultCloseFailsafeTimeout = TimeSpan.FromSeconds(30);

    private readonly WindowArranger _windowArranger;
    private readonly Dictionary<string, QuadrantVeilWindow> _veils = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, MonitorBackdropWindow> _backdrops = new();
    private readonly Dictionary<string, int> _slotMonitors = new(StringComparer.OrdinalIgnoreCase);
    private CenterHudWindow? _hudWindow;
    private DispatcherTimer? _failsafeTimer;
    private int _sessionGeneration;
    private bool _isActive;
    private bool _animationsEnabled = true;

    internal sealed record RevealWaveTiming(
        int Index,
        double GlowStartMs,
        double UncloakAtMs,
        double FadeStartMs,
        double FadeDurationMs);

    // 1 つ 1 つ落ち着いてランプが灯っていくウェーブ時刻表を生成する（テスト対象）。
    internal static IReadOnlyList<RevealWaveTiming> BuildRevealWaveTimings(int slotCount)
    {
        if (slotCount <= 0)
        {
            return [];
        }

        var list = new List<RevealWaveTiming>(slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            var glowStartMs = WaveStaggerDelay.TotalMilliseconds * i;
            list.Add(new RevealWaveTiming(
                i,
                GlowStartMs: glowStartMs,
                UncloakAtMs: glowStartMs + WaveUncloakLeadDuration.TotalMilliseconds,
                FadeStartMs: glowStartMs + WaveGlowLeadDuration.TotalMilliseconds,
                FadeDurationMs: WaveVeilFadeOutDuration.TotalMilliseconds));
        }

        return list;
    }

    public LaunchStagingOverlay(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
    }

    public bool IsActive => _isActive;

    // 起動用: 対象象限へ完全不透明のダークガラスベールと中央 HUD をふわっと被せ切る。
    public async Task BeginForLaunchAsync(
        IReadOnlyList<LaunchStageSlotEntry> slotEntries,
        IReadOnlyList<WindowSlot> allSlots,
        int gap,
        int baseMonitorIndex,
        int hudMonitorIndex,
        IntPtr panelHandle,
        string headline,
        string statusMessage,
        bool animate,
        TimeSpan? failsafeTimeout = null)
    {
        var targets = new List<StagingVeilTarget>();
        foreach (var entry in slotEntries.Where(item => item.IsTarget))
        {
            if (entry.SlotIndex < 0 || entry.SlotIndex >= allSlots.Count)
            {
                continue;
            }

            var slot = allSlots[entry.SlotIndex];
            if (!_windowArranger.TryGetSlotCellBounds(slot, entry.SlotIndex, gap, baseMonitorIndex, out var cellBounds))
            {
                continue;
            }

            var monitorIndex = slot.MonitorOverride ?? baseMonitorIndex;
            targets.Add(new StagingVeilTarget(
                entry.SlotName,
                entry.DisplayTitle,
                entry.ApplicationName,
                cellBounds,
                entry.State,
                monitorIndex));
        }

        var sessionId = BeginCore(
            slotEntries,
            targets,
            hudMonitorIndex,
            panelHandle,
            headline,
            statusMessage,
            animate,
            isClosingMode: false,
            failsafeTimeout ?? DefaultLaunchFailsafeTimeout);

        if (!_isActive || sessionId != _sessionGeneration || !animate || (_veils.Count == 0 && _backdrops.Count == 0))
        {
            return;
        }

        await WaitOneRenderPassAsync();
        if (!_isActive || sessionId != _sessionGeneration)
        {
            return;
        }

        var fadeTasks = new List<Task>(_veils.Count + _backdrops.Count);
        foreach (var backdrop in _backdrops.Values)
        {
            fadeTasks.Add(backdrop.FadeOpacityAsync(0.0, 1.0, LaunchCoverFadeInDuration, TimeSpan.Zero));
        }

        foreach (var veil in _veils.Values)
        {
            fadeTasks.Add(veil.FadeOpacityAsync(0.0, 1.0, LaunchCoverFadeInDuration, TimeSpan.Zero));
        }

        _ = await Task.WhenAny(
            Task.WhenAll(fadeTasks),
            Task.Delay(LaunchCoverFadeInDuration + TimeSpan.FromMilliseconds(600)));
    }

    // 終了用: 開いている全対象へ完全不透明ベールと黒い全画面シールド、中央 HUD を一斉に被せ切る。
    public async Task BeginCoverForCloseAsync(
        IReadOnlyList<LaunchStageSlotEntry> slotEntries,
        IReadOnlyList<StagingVeilTarget> veilTargets,
        int hudMonitorIndex,
        IntPtr panelHandle,
        string headline,
        string statusMessage,
        bool animate,
        TimeSpan? failsafeTimeout = null)
    {
        var sessionId = BeginCore(
            slotEntries,
            veilTargets,
            hudMonitorIndex,
            panelHandle,
            headline,
            statusMessage,
            animate,
            isClosingMode: true,
            failsafeTimeout ?? DefaultCloseFailsafeTimeout);

        if (!_isActive || sessionId != _sessionGeneration || !animate || (_veils.Count == 0 && _backdrops.Count == 0))
        {
            return;
        }

        await WaitOneRenderPassAsync();
        if (!_isActive || sessionId != _sessionGeneration)
        {
            return;
        }

        var fadeTasks = new List<Task>(_veils.Count + _backdrops.Count);
        foreach (var backdrop in _backdrops.Values)
        {
            fadeTasks.Add(backdrop.FadeOpacityAsync(0.0, 1.0, CloseCoverFadeInDuration, TimeSpan.Zero));
        }

        foreach (var veil in _veils.Values)
        {
            fadeTasks.Add(veil.FadeOpacityAsync(0.0, 1.0, CloseCoverFadeInDuration, TimeSpan.Zero));
        }

        _ = await Task.WhenAny(
            Task.WhenAll(fadeTasks),
            Task.Delay(CloseCoverFadeInDuration + TimeSpan.FromMilliseconds(600)));
    }

    private int BeginCore(
        IReadOnlyList<LaunchStageSlotEntry> slotEntries,
        IReadOnlyList<StagingVeilTarget> veilTargets,
        int hudMonitorIndex,
        IntPtr panelHandle,
        string headline,
        string statusMessage,
        bool animate,
        bool isClosingMode,
        TimeSpan failsafeTimeout)
    {
        DismissImmediate();

        var sessionId = ++_sessionGeneration;
        _animationsEnabled = animate;
        _isActive = true;
        ArmFailsafeTimer(sessionId, failsafeTimeout);

        try
        {
            var isBatch = slotEntries.Count(item => item.IsTarget) > 1;
            var currentInsertAfter = _windowArranger.IsInTopmostLayer(panelHandle) ? panelHandle : IntPtr.Zero;

            // 1. まず 4 分割の象限ベールをパネルのすぐ後ろへ順番にチェーン配置する。
            //    (Panel > VeilA > VeilB > VeilC > VeilD の順を保つことで、全ベールが確実に黒シールドより前に出る)
            foreach (var target in veilTargets)
            {
                var veil = new QuadrantVeilWindow(
                    target.SlotName,
                    target.DisplayTitle,
                    target.ApplicationName,
                    isBatch,
                    isClosingMode,
                    target.Bounds,
                    currentInsertAfter,
                    initialOpacity: animate ? 0.0 : 1.0);
                veil.ShowVeil();
                if (isClosingMode)
                {
                    veil.UpdateState(target.InitialState);
                }

                _veils[target.SlotName] = veil;
                _slotMonitors[target.SlotName] = target.MonitorIndex;
                if (veil.Handle != IntPtr.Zero)
                {
                    currentInsertAfter = veil.Handle;
                }
            }

            // 2. 全象限ベールのすぐ背面（かつ裏の実ウィンドウの前面）へ黒い全画面シールドを挟み、
            //    4 分割の十字隙間・角丸の隅・外周からのチラつきを 1px も漏らさず完全遮断する。
            if (ShouldUseFullScreenBackdrop(isClosingMode, veilTargets.Count))
            {
                var targetMonitors = veilTargets
                    .Select(target => target.MonitorIndex)
                    .Distinct()
                    .ToList();
                foreach (var monitorIndex in targetMonitors)
                {
                    if (!_windowArranger.TryGetMonitorWorkArea(monitorIndex, out var workArea)
                        || workArea.Width <= 0
                        || workArea.Height <= 0)
                    {
                        continue;
                    }

                    var backdrop = new MonitorBackdropWindow(
                        workArea,
                        currentInsertAfter,
                        initialOpacity: animate ? 0.0 : 1.0);
                    backdrop.ShowBackdrop();
                    _backdrops[monitorIndex] = backdrop;
                }
            }

            // 3. 最前面に中央 HUD を表示する。
            _hudWindow = new CenterHudWindow(
                slotEntries,
                headline,
                statusMessage,
                isClosingMode,
                _windowArranger,
                hudMonitorIndex);
            _hudWindow.ShowHud(animate);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            DismissImmediate();
        }

        return sessionId;
    }

    private void ArmFailsafeTimer(int sessionId, TimeSpan timeout)
    {
        StopFailsafeTimer();
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _failsafeTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = timeout
        };
        _failsafeTimer.Tick += (_, _) =>
        {
            StopFailsafeTimer();
            if (_isActive && sessionId == _sessionGeneration)
            {
                DiagnosticLog.Write(
                    LogLevel.Warn,
                    $"LaunchStagingOverlay failsafe triggered after {timeout.TotalSeconds:0.#}s; forcibly clearing masks and backdrop.");
                DismissImmediate();
            }
        };
        _failsafeTimer.Start();
    }

    private void StopFailsafeTimer()
    {
        if (_failsafeTimer is not null)
        {
            _failsafeTimer.Stop();
            _failsafeTimer = null;
        }
    }

    // テスト専用の無菌フェイルセーフ検証ヘルパー（ウィンドウを表示せずに自動解除タイマーの動作を検証）。
    internal void ArmFailsafeForTesting(TimeSpan timeout)
    {
        DismissImmediate();
        var sessionId = ++_sessionGeneration;
        _isActive = true;
        ArmFailsafeTimer(sessionId, timeout);
    }

    public void UpdateSlotState(string slotName, LaunchStageSlotState state, string? statusMessage = null)
    {
        if (!_isActive)
        {
            return;
        }

        try
        {
            if (_veils.TryGetValue(slotName, out var veil))
            {
                veil.UpdateState(state);
            }

            _hudWindow?.UpdateSlotState(slotName, state, statusMessage);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    public void UpdateStatusMessage(string statusMessage)
    {
        if (!_isActive)
        {
            return;
        }

        try
        {
            _hudWindow?.UpdateStatusMessage(statusMessage);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // 閉じる処理中に未保存確認ダイアログ等で長時間止まったスロットだけ、後からベールを外す安全弁。
    // 同じモニタの黒い全画面シールドも一緒に外し、確認ダイアログが裏に隠れないようにする。
    // 最大化全面カバーの裏で別スロットが止まった場合も、全黒シールドと全面カバーを確実に解除する。
    public void ReleaseSlotVeilEarly(string slotName)
    {
        if (!_isActive)
        {
            return;
        }

        var releasedSpecificMonitor = false;
        if (_slotMonitors.TryGetValue(slotName, out var monitorIndex)
            && _backdrops.TryGetValue(monitorIndex, out var backdrop))
        {
            releasedSpecificMonitor = true;
            _backdrops.Remove(monitorIndex);
            ReleaseBackdropSafely(backdrop);
        }

        if (_veils.TryGetValue(slotName, out var veil))
        {
            _veils.Remove(slotName);
            ReleaseVeilSafely(veil);
            return;
        }

        // 最大化全面カバー中などで対象スロット自身の個別ベールが存在しない場合は、
        // 確認ダイアログが黒幕・全面カバーの裏に隠れないよう残存カバーと黒シールドをすべて解放する。
        if (!releasedSpecificMonitor)
        {
            foreach (var extraBackdrop in _backdrops.Values.ToList())
            {
                ReleaseBackdropSafely(extraBackdrop);
            }

            _backdrops.Clear();
            foreach (var extraVeil in _veils.Values.ToList())
            {
                ReleaseVeilSafely(extraVeil);
            }

            _veils.Clear();
        }
    }

    private void ReleaseBackdropSafely(MonitorBackdropWindow backdrop)
    {
        if (!_animationsEnabled)
        {
            SafeCloseWindow(backdrop);
        }
        else
        {
            _ = CloseBackdropWithFadeAsync(backdrop, TimeSpan.FromMilliseconds(220));
        }
    }

    private void ReleaseVeilSafely(QuadrantVeilWindow veil)
    {
        if (!_animationsEnabled)
        {
            SafeCloseWindow(veil);
        }
        else
        {
            _ = CloseVeilWithFadeAsync(veil, TimeSpan.FromMilliseconds(260));
        }
    }

    // 起動リビール（1 つ 1 つ落ち着いて LED ランプが灯るウェーブ）:
    // 全員整ったあとに静かな一拍を置き、WPF GPU 独立クロックの BeginTime 一括タイムラインで
    // A→B→C→D へ 480ms ずつ間隔を空けながら、ほのかな LED 光漏れ点灯→580ms の S 字開放を滑らかに実行する。
    public async Task RevealSequentiallyAndCloseAsync(
        IReadOnlyList<string> orderedSlotNames,
        Action<string> releaseSlotCloak,
        Action releaseRemainingCloaks,
        string readyHeadline,
        string readyMessage)
    {
        var remainingReleased = false;
        void EnsureRemainingCloaksReleased()
        {
            if (remainingReleased)
            {
                return;
            }

            remainingReleased = true;
            try
            {
                releaseRemainingCloaks();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        if (!_isActive)
        {
            EnsureRemainingCloaksReleased();
            return;
        }

        try
        {
            var sessionId = _sessionGeneration;
            if (!_animationsEnabled)
            {
                EnsureRemainingCloaksReleased();
                DismissImmediate();
                return;
            }

            var slotsToReveal = orderedSlotNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _hudWindow?.ShowReadyEmphasis(readyHeadline, readyMessage);

            // 全員の起動・整列・安定化が終わった直後の静かな一拍（260ms / 120ms）の間に、
            // 背面の黒い全画面シールドだけをスッと先に抜いておく。
            // この時点で 4 枚の象限ベールは 100% 不透明のまま裏の静止ウィンドウを完全に覆っているため
            // チラつきは起きず、直後の A→B→C→D の 1 つずつ点灯で各画面が黒幕に塞がれず鮮やかに現れる。
            var holdDuration = slotsToReveal.Count > 1 ? PreWaveBatchHoldDuration : PreWaveSingleHoldDuration;
            if (_backdrops.Count > 0)
            {
                var backdropFadeDuration = TimeSpan.FromMilliseconds(Math.Min(BackdropPreRevealFadeOutDuration.TotalMilliseconds, holdDuration.TotalMilliseconds));
                var backdropFadeTasks = _backdrops.Values
                    .Select(backdrop => backdrop.FadeOpacityAsync(backdrop.CurrentOpacity, 0.0, backdropFadeDuration, TimeSpan.Zero))
                    .ToList();
                var waitAllBackdrops = Task.WhenAll(backdropFadeTasks);
                await Task.WhenAll(
                    Task.Delay(holdDuration),
                    Task.WhenAny(waitAllBackdrops, Task.Delay(holdDuration + TimeSpan.FromMilliseconds(200))));
                if (!_isActive || sessionId != _sessionGeneration)
                {
                    return;
                }

                CloseAllBackdropsImmediate();
            }
            else
            {
                await Task.Delay(holdDuration);
                if (!_isActive || sessionId != _sessionGeneration)
                {
                    return;
                }
            }

            var timings = BuildRevealWaveTimings(slotsToReveal.Count);
            var animationTasks = new List<Task>();

            for (var index = 0; index < slotsToReveal.Count; index++)
            {
                var slotName = slotsToReveal[index];
                var timing = timings[index];
                var glowBegin = TimeSpan.FromMilliseconds(timing.GlowStartMs);
                var uncloakDelay = TimeSpan.FromMilliseconds(timing.UncloakAtMs);
                var fadeBegin = TimeSpan.FromMilliseconds(timing.FadeStartMs);
                var fadeDuration = TimeSpan.FromMilliseconds(timing.FadeDurationMs);

                // 1. HUD チップと象限ベールの LED ランプ点灯を BeginTime 付きで GPU タイムラインへ一括投入。
                _hudWindow?.PulseSlotGentleLed(slotName, glowBegin);
                if (_veils.TryGetValue(slotName, out var veil))
                {
                    veil.PlayGentleLedBloom(glowBegin);
                    animationTasks.Add(veil.FadeOpacityAsync(veil.CurrentOpacity, 0.0, fadeDuration, fadeBegin));
                }

                // 2. 万一クロークされているウィンドウがあれば、幕が開き始める直前に静かに解除する。
                animationTasks.Add(ReleaseSlotCloakAfterDelayAsync(slotName, uncloakDelay, releaseSlotCloak));
            }

            // 対象外の残余ベールがあれば最後のウェーブに合わせて静かに晴らす。
            var revealedSet = new HashSet<string>(slotsToReveal, StringComparer.OrdinalIgnoreCase);
            var lastTiming = timings.Count > 0 ? timings[^1] : null;
            var fallbackFadeBegin = TimeSpan.FromMilliseconds(lastTiming?.FadeStartMs ?? WaveGlowLeadDuration.TotalMilliseconds);
            foreach (var extra in _veils.Where(pair => !revealedSet.Contains(pair.Key)).ToList())
            {
                animationTasks.Add(extra.Value.FadeOpacityAsync(extra.Value.CurrentOpacity, 0.0, WaveVeilFadeOutDuration, fallbackFadeBegin));
            }

            // 3. 中央 HUD は最後のランプ点灯後の余韻から同じ GPU タイムライン上で静かに溶け消える。
            var hudFadeBegin = TimeSpan.FromMilliseconds(
                (lastTiming?.GlowStartMs ?? 0.0) + HudFinalFadeOutDelayAfterLastGlow.TotalMilliseconds);
            if (_hudWindow is { } hud)
            {
                animationTasks.Add(hud.FadeOutAsync(HudFinalFadeOutDuration, hudFadeBegin, expandSlightly: true));
            }

            var maxExpectedMs = Math.Max(
                lastTiming is not null ? lastTiming.FadeStartMs + lastTiming.FadeDurationMs : WaveVeilFadeOutDuration.TotalMilliseconds,
                hudFadeBegin.TotalMilliseconds + HudFinalFadeOutDuration.TotalMilliseconds);

            _ = await Task.WhenAny(
                Task.WhenAll(animationTasks),
                Task.Delay(TimeSpan.FromMilliseconds(maxExpectedMs + 1400)));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            EnsureRemainingCloaksReleased();
            DismissImmediate();
        }
    }

    public Task RevealAndCloseAsync(Action releaseCloaks, string readyHeadline, string readyMessage)
    {
        return RevealSequentiallyAndCloseAsync(
            _veils.Keys.ToList(),
            _ => { },
            releaseCloaks,
            readyHeadline,
            readyMessage);
    }

    // 終了完了フィニッシュ:
    // 裏での個別終了がすべて終わったあと、人間が「すべて完了した」と知覚できるワンクッションを
    // しっかり置いてから、全象限ベールと中央 HUD を同じ瞬間に「フッ」と軽やかに一斉消散させる。
    public async Task VanishAllAndCloseAsync(string doneHeadline, string doneMessage)
    {
        if (!_isActive)
        {
            return;
        }

        try
        {
            var sessionId = _sessionGeneration;
            if (!_animationsEnabled)
            {
                DismissImmediate();
                return;
            }

            _hudWindow?.ShowCloseDoneEmphasis(doneHeadline, doneMessage);
            foreach (var veil in _veils.Values)
            {
                veil.PlayGentleCloseFadeGlow();
            }

            var holdDuration = GetCloseDoneHoldDuration(_veils.Count > 1);
            await Task.Delay(holdDuration);
            if (!_isActive || sessionId != _sessionGeneration)
            {
                return;
            }

            var fadeTasks = new List<Task>(_veils.Count + _backdrops.Count + 1);
            foreach (var backdrop in _backdrops.Values)
            {
                fadeTasks.Add(backdrop.FadeOpacityAsync(
                    backdrop.CurrentOpacity,
                    0.0,
                    CloseVanishFadeOutDuration,
                    TimeSpan.Zero,
                    briskVanish: true));
            }

            foreach (var veil in _veils.Values)
            {
                fadeTasks.Add(veil.FadeOpacityAsync(
                    veil.CurrentOpacity,
                    0.0,
                    CloseVanishFadeOutDuration,
                    TimeSpan.Zero,
                    briskVanish: true));
            }

            if (_hudWindow is { } hud)
            {
                fadeTasks.Add(hud.FadeOutAsync(
                    CloseVanishFadeOutDuration,
                    TimeSpan.Zero,
                    expandSlightly: false,
                    briskVanish: true));
            }

            _ = await Task.WhenAny(
                Task.WhenAll(fadeTasks),
                Task.Delay(CloseVanishFadeOutDuration + TimeSpan.FromMilliseconds(900)));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            DismissImmediate();
        }
    }

    public async Task DismissAsync()
    {
        if (!_isActive)
        {
            return;
        }

        try
        {
            if (_animationsEnabled)
            {
                var fadeTasks = new List<Task>(_veils.Count + _backdrops.Count + 1);
                foreach (var backdrop in _backdrops.Values)
                {
                    fadeTasks.Add(backdrop.FadeOpacityAsync(backdrop.CurrentOpacity, 0.0, DismissFadeOutDuration, TimeSpan.Zero));
                }

                foreach (var veil in _veils.Values)
                {
                    fadeTasks.Add(veil.FadeOpacityAsync(veil.CurrentOpacity, 0.0, DismissFadeOutDuration, TimeSpan.Zero));
                }

                if (_hudWindow is { } hud)
                {
                    fadeTasks.Add(hud.FadeOutAsync(DismissFadeOutDuration, TimeSpan.Zero, expandSlightly: false));
                }

                _ = await Task.WhenAny(
                    Task.WhenAll(fadeTasks),
                    Task.Delay(DismissFadeOutDuration + TimeSpan.FromMilliseconds(600)));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            DismissImmediate();
        }
    }

    public void DismissImmediate()
    {
        StopFailsafeTimer();
        _sessionGeneration++;
        _isActive = false;

        foreach (var veil in _veils.Values)
        {
            SafeCloseWindow(veil);
        }

        _veils.Clear();
        CloseAllBackdropsImmediate();
        _slotMonitors.Clear();

        if (_hudWindow is not null)
        {
            SafeCloseWindow(_hudWindow);
            _hudWindow = null;
        }
    }

    private void CloseAllBackdropsImmediate()
    {
        foreach (var backdrop in _backdrops.Values)
        {
            SafeCloseWindow(backdrop);
        }

        _backdrops.Clear();
    }

    public void Dispose()
    {
        DismissImmediate();
    }

    private static async Task WaitOneRenderPassAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        await Task.Delay(35);
    }

    private async Task ReleaseSlotCloakAfterDelayAsync(string slotName, TimeSpan delay, Action<string> releaseSlotCloak)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        try
        {
            releaseSlotCloak(slotName);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static async Task CloseVeilWithFadeAsync(QuadrantVeilWindow veil, TimeSpan duration)
    {
        try
        {
            _ = await Task.WhenAny(
                veil.FadeOpacityAsync(veil.CurrentOpacity, 0.0, duration, TimeSpan.Zero),
                Task.Delay(duration + TimeSpan.FromMilliseconds(400)));
        }
        catch
        {
            // Ignore animation failures during early release.
        }
        finally
        {
            SafeCloseWindow(veil);
        }
    }

    private static async Task CloseBackdropWithFadeAsync(MonitorBackdropWindow backdrop, TimeSpan duration)
    {
        try
        {
            _ = await Task.WhenAny(
                backdrop.FadeOpacityAsync(backdrop.CurrentOpacity, 0.0, duration, TimeSpan.Zero),
                Task.Delay(duration + TimeSpan.FromMilliseconds(350)));
        }
        catch
        {
            // Ignore animation failures during early release.
        }
        finally
        {
            SafeCloseWindow(backdrop);
        }
    }

    // テスト専用の無菌検証ヘルパー。画面へ Show() せずに中央 HUD・象限ベール・黒い全画面シールドの
    // ビジュアルツリー構築・レイアウト計測・全状態遷移（起動モード・終了モード双方）を確認する。
    internal static (Size HudSize, Size VeilSize, bool VeilAllowsTransparency, Size BackdropSize, bool BackdropAllowsTransparency) VerifyVisualTreeOffscreenForTesting(
        IReadOnlyList<LaunchStageSlotEntry> entries,
        string headline,
        string statusMessage,
        bool isClosingMode = false)
    {
        var arranger = new WindowArranger();
        var hud = new CenterHudWindow(entries, headline, statusMessage, isClosingMode, arranger, monitorIndex: 0);
        var hudContent = (UIElement)hud.Content;
        hudContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        hudContent.Arrange(new Rect(hudContent.DesiredSize));

        foreach (var entry in entries.Where(item => item.IsTarget))
        {
            if (isClosingMode)
            {
                hud.UpdateSlotState(entry.SlotName, LaunchStageSlotState.Closing, "終了処理中...");
                hud.UpdateSlotState(entry.SlotName, LaunchStageSlotState.Closed, "終了完了");
            }
            else
            {
                hud.UpdateSlotState(entry.SlotName, LaunchStageSlotState.Launching, "起動中...");
                hud.UpdateSlotState(entry.SlotName, LaunchStageSlotState.Ready, "準備完了");
                hud.PulseSlotGentleLed(entry.SlotName, TimeSpan.Zero);
            }
        }

        if (isClosingMode)
        {
            hud.ShowCloseDoneEmphasis("QUARTET CLOSED", "終了しました");
        }
        else
        {
            hud.ShowReadyEmphasis("QUARTET READY", "表示します");
        }

        var firstTarget = entries.First(item => item.IsTarget);
        var veil = new QuadrantVeilWindow(
            firstTarget.SlotName,
            firstTarget.DisplayTitle,
            firstTarget.ApplicationName,
            isBatch: entries.Count(item => item.IsTarget) > 1,
            isClosingMode,
            new WindowArranger.WindowBounds(0, 0, 950, 520),
            IntPtr.Zero,
            initialOpacity: 1.0);
        var veilContent = (UIElement)veil.Content;
        veilContent.Measure(new Size(950, 520));
        veilContent.Arrange(new Rect(0, 0, 950, 520));
        if (isClosingMode)
        {
            veil.UpdateState(LaunchStageSlotState.Closing);
            veil.UpdateState(LaunchStageSlotState.Closed);
            veil.PlayGentleCloseFadeGlow();
        }
        else
        {
            veil.UpdateState(LaunchStageSlotState.Launching);
            veil.UpdateState(LaunchStageSlotState.Ready);
            veil.PlayGentleLedBloom(TimeSpan.Zero);
            veil.UpdateState(LaunchStageSlotState.Failed);
        }

        var backdrop = new MonitorBackdropWindow(
            new WindowArranger.WindowBounds(0, 0, 1920, 1040),
            IntPtr.Zero,
            initialOpacity: 1.0);
        var backdropContent = (UIElement)backdrop.Content;
        backdropContent.Measure(new Size(1920, 1040));
        backdropContent.Arrange(new Rect(0, 0, 1920, 1040));

        return (hudContent.DesiredSize, veilContent.DesiredSize, veil.AllowsTransparency, backdropContent.RenderSize, backdrop.AllowsTransparency);
    }

    private static void SafeCloseWindow(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // WPF の Timeline.BeginTime は null にすると「永遠に開始しない」仕様になるため、
    // 0 以下の指定は必ず TimeSpan.Zero へ正規化して即時開始を保証する（テスト対象）。
    internal static TimeSpan NormalizeBeginTime(TimeSpan beginTime)
    {
        return beginTime > TimeSpan.Zero ? beginTime : TimeSpan.Zero;
    }

    private static SolidColorBrush CreateFrozenSolid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // -------------------------------------------------------------------------
    // 1. 象限ベールウィンドウ（正式 AllowsTransparency=True + GPU DoubleAnimation）
    // -------------------------------------------------------------------------
    private sealed class QuadrantVeilWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly bool _isClosingMode;
        private readonly WindowArranger.WindowBounds _bounds;
        private readonly IntPtr _insertAfterHandle;
        private readonly Border _veilRoot;
        private readonly Border _ambientBloomLayer;
        private readonly Grid _softEdgeGlowHost;
        private readonly Border _frameBorder;
        private readonly Border _ledHighlightFrame;
        private readonly StackPanel _centerStack;
        private readonly TextBlock _slotLetterText;
        private readonly TextBlock _stateText;
        private readonly List<Shape> _cornerAccents = [];
        private IntPtr _handle;
        private bool _closed;

        public IntPtr Handle => _handle;
        public double CurrentOpacity => _veilRoot.Opacity;

        public QuadrantVeilWindow(
            string slotName,
            string displayTitle,
            string applicationName,
            bool isBatch,
            bool isClosingMode,
            WindowArranger.WindowBounds bounds,
            IntPtr insertAfterHandle,
            double initialOpacity)
        {
            _ = isBatch;
            _isClosingMode = isClosingMode;
            _bounds = bounds;
            _insertAfterHandle = insertAfterHandle;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            UseLayoutRounding = false;

            var rootGrid = new Grid();

            // 1. 中央からほのかに滲み出る LED ソフトオーラ（BlurEffect 無し・純グラデーション）
            var ambientBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.66,
                RadiusY = 0.66,
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(0x46, 0x45, 0xD4, 0x83), 0.0),
                    new GradientStop(Color.FromArgb(0x20, 0x45, 0xD4, 0x83), 0.54),
                    new GradientStop(Color.FromArgb(0x00, 0x45, 0xD4, 0x83), 1.0)
                ]
            };
            ambientBrush.Freeze();

            _ambientBloomLayer = new Border
            {
                CornerRadius = new CornerRadius(10),
                Opacity = isClosingMode ? 0.20 : 0.0,
                Background = ambientBrush
            };
            rootGrid.Children.Add(_ambientBloomLayer);

            // 2. 四辺から内側へほのかに光が漏れる純グラデーション・インナーグロウ
            _softEdgeGlowHost = CreatePureGradientEdgeGlow(initialOpacity: isClosingMode ? 0.22 : 0.0);
            rootGrid.Children.Add(_softEdgeGlowHost);

            _frameBorder = new Border
            {
                BorderBrush = CreateFrozenSolid(Color.FromArgb(0x56, 0x45, 0xD4, 0x83)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };
            rootGrid.Children.Add(_frameBorder);

            _ledHighlightFrame = new Border
            {
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x88, 0xF0, 0xB4)),
                BorderThickness = new Thickness(1.6),
                CornerRadius = new CornerRadius(10),
                Opacity = 0.0
            };
            rootGrid.Children.Add(_ledHighlightFrame);

            AddCornerBracket(rootGrid, HorizontalAlignment.Left, VerticalAlignment.Top, 1, 1);
            AddCornerBracket(rootGrid, HorizontalAlignment.Right, VerticalAlignment.Top, -1, 1);
            AddCornerBracket(rootGrid, HorizontalAlignment.Left, VerticalAlignment.Bottom, 1, -1);
            AddCornerBracket(rootGrid, HorizontalAlignment.Right, VerticalAlignment.Bottom, -1, -1);

            _centerStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _slotLetterText = new TextBlock
            {
                Text = slotName,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 40,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateFrozenSolid(Color.FromArgb(0x62, 0x45, 0xD4, 0x83)),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var titleText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(displayTitle) ? $"スロット {slotName}" : displayTitle,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateFrozenSolid(Color.FromArgb(0xA4, 0xEE, 0xF4, 0xEF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(16, 4, 16, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340
            };

            _stateText = new TextBlock
            {
                Text = isClosingMode
                    ? $"{applicationName} を閉じています..."
                    : $"{applicationName} を待機しています...",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = CreateFrozenSolid(Color.FromArgb(0xB4, 0x9A, 0xA5, 0x9E)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            _centerStack.Children.Add(_slotLetterText);
            _centerStack.Children.Add(titleText);
            _centerStack.Children.Add(_stateText);
            rootGrid.Children.Add(_centerStack);

            _veilRoot = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x0B, 0x10, 0x0D)),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Opacity = Math.Clamp(initialOpacity, 0.0, 1.0),
                Child = rootGrid
            };

            Content = _veilRoot;
            Closed += (_, _) => _closed = true;
        }

        public void ShowVeil()
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            var exStyle = GetWindowLong(_handle, GwlExStyle);
            // AllowsTransparency=True で WPF が付与済みの WS_EX_LAYERED を保ったままクリックスルー属性を足す。
            _ = SetWindowLong(_handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);

            PlaceWindow(_handle);
            Show();
            PlaceWindow(_handle);
            UpdateLayout();
        }

        public Task FadeOpacityAsync(
            double fromOpacity,
            double toOpacity,
            TimeSpan duration,
            TimeSpan beginTime,
            bool briskVanish = false)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            var clampedFrom = Math.Clamp(fromOpacity, 0.0, 1.0);
            var clampedTo = Math.Clamp(toOpacity, 0.0, 1.0);
            if (duration <= TimeSpan.Zero)
            {
                _veilRoot.BeginAnimation(OpacityProperty, null);
                _veilRoot.Opacity = clampedTo;
                return Task.CompletedTask;
            }

            // BeginTime 待機中に前の HoldEnd アニメーションが破棄されて初期ローカル値（0.0）へ
            // 巻き戻るのを防ぐため、開始時の現在値をローカル値へ確定してから BeginAnimation する。
            _veilRoot.Opacity = clampedFrom;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            IEasingFunction ease = briskVanish
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new SineEase { EasingMode = EasingMode.EaseInOut };
            var anim = new DoubleAnimation(
                clampedFrom,
                clampedTo,
                new Duration(duration))
            {
                BeginTime = NormalizeBeginTime(beginTime),
                EasingFunction = ease
            };
            // Completed は必ず BeginAnimation より前に登録する。
            anim.Completed += (_, _) =>
            {
                if (!_closed)
                {
                    _veilRoot.Opacity = clampedTo;
                }

                tcs.TrySetResult(true);
            };
            _veilRoot.BeginAnimation(OpacityProperty, anim);
            return tcs.Task;
        }

        public void UpdateState(LaunchStageSlotState state)
        {
            if (_closed)
            {
                return;
            }

            switch (state)
            {
                case LaunchStageSlotState.Launching:
                    _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0x88, 0x45, 0xD4, 0x83));
                    _slotLetterText.Foreground = CreateFrozenSolid(Color.FromArgb(0xAA, 0x67, 0xE7, 0xA4));
                    _stateText.Text = "準備しています...";
                    _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0xAE, 0xC9, 0xB8));
                    SetAccentStroke(Color.FromArgb(0x9E, 0x45, 0xD4, 0x83));
                    StartBreathingGlow(0.08, 0.24, 0.06, 0.18);
                    break;
                case LaunchStageSlotState.Ready:
                    _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0xA8, 0x45, 0xD4, 0x83));
                    _slotLetterText.Foreground = CreateFrozenSolid(Color.FromArgb(0xC8, 0x74, 0xE8, 0xA8));
                    _stateText.Text = "準備完了";
                    _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0x8E, 0xD6, 0xAC));
                    SetAccentStroke(Color.FromArgb(0xB8, 0x67, 0xE7, 0xA4));
                    // 本番の 1 つずつランプ点灯を引き立てるため、待機中は控えめな灯火へ落ち着かせる。
                    FadeGlowTo(0.22, 0.16, TimeSpan.FromMilliseconds(460));
                    break;
                case LaunchStageSlotState.Closing:
                    _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0x7A, 0x45, 0xD4, 0x83));
                    _slotLetterText.Foreground = CreateFrozenSolid(Color.FromArgb(0x98, 0x74, 0xE8, 0xA8));
                    _stateText.Text = "閉じています...";
                    _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0xAE, 0xC4, 0xB7));
                    SetAccentStroke(Color.FromArgb(0x8A, 0x45, 0xD4, 0x83));
                    StartBreathingGlow(0.08, 0.22, 0.06, 0.16);
                    break;
                case LaunchStageSlotState.Closed:
                    _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0x46, 0x45, 0xD4, 0x83));
                    _slotLetterText.Foreground = CreateFrozenSolid(Color.FromArgb(0x5A, 0x74, 0xE8, 0xA8));
                    _stateText.Text = "終了完了";
                    _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0x88, 0xA6, 0x95));
                    SetAccentStroke(Color.FromArgb(0x58, 0x45, 0xD4, 0x83));
                    FadeGlowTo(0.08, 0.06, TimeSpan.FromMilliseconds(420));
                    break;
                case LaunchStageSlotState.Failed:
                    _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0x78, 0xA6, 0x53, 0x48));
                    _slotLetterText.Foreground = CreateFrozenSolid(Color.FromArgb(0x88, 0xF2, 0xB7, 0xAF));
                    _stateText.Text = _isClosingMode ? "確認ダイアログを待機しています" : "起動を確認できませんでした";
                    _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0xF2, 0xB7, 0xAF));
                    SetAccentStroke(Color.FromArgb(0x88, 0xA6, 0x53, 0x48));
                    FadeGlowTo(0.0, 0.0, TimeSpan.FromMilliseconds(320));
                    break;
            }
        }

        // 本番の「1 つ 1 つランプがつく」点灯演出（BeginTime 付きで GPU タイムラインへ一括投入）。
        public void PlayGentleLedBloom(TimeSpan beginTime)
        {
            if (_closed)
            {
                return;
            }

            var begin = NormalizeBeginTime(beginTime);
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

            _centerStack.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(320)))
                {
                    BeginTime = begin,
                    EasingFunction = sine
                });

            var edgeBloom = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            edgeBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = sine });
            edgeBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.34, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(780))) { EasingFunction = sine });
            _softEdgeGlowHost.BeginAnimation(OpacityProperty, edgeBloom);

            var centerBloom = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            centerBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = sine });
            centerBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.24, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(780))) { EasingFunction = sine });
            _ambientBloomLayer.BeginAnimation(OpacityProperty, centerBloom);

            var frameBloom = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            frameBloom.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frameBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))) { EasingFunction = sine });
            frameBloom.KeyFrames.Add(new EasingDoubleKeyFrame(0.20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(780))) { EasingFunction = sine });
            _ledHighlightFrame.BeginAnimation(OpacityProperty, frameBloom);
        }

        // 全員の終了完了を知覚させる静かな収束サイン（文字を「✓ 終了完了」へ整え、淡い輪郭光がすっと引く）。
        public void PlayGentleCloseFadeGlow()
        {
            if (_closed)
            {
                return;
            }

            _stateText.Text = "✓ 終了完了";
            _stateText.Foreground = CreateFrozenSolid(Color.FromRgb(0x9E, 0xDF, 0xBA));
            _frameBorder.BorderBrush = CreateFrozenSolid(Color.FromArgb(0x6C, 0x45, 0xD4, 0x83));

            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            var settleFrame = new DoubleAnimationUsingKeyFrames();
            settleFrame.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            settleFrame.KeyFrames.Add(new EasingDoubleKeyFrame(0.42, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))) { EasingFunction = sine });
            settleFrame.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(540))) { EasingFunction = sine });
            _ledHighlightFrame.BeginAnimation(OpacityProperty, settleFrame);

            _softEdgeGlowHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(480))) { EasingFunction = sine });
            _ambientBloomLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(480))) { EasingFunction = sine });
        }

        private void StartBreathingGlow(double edgeMin, double edgeMax, double bloomMin, double bloomMax)
        {
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            _softEdgeGlowHost.BeginAnimation(OpacityProperty, new DoubleAnimation(edgeMin, edgeMax, new Duration(TimeSpan.FromMilliseconds(1800)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = sine
            });
            _ambientBloomLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(bloomMin, bloomMax, new Duration(TimeSpan.FromMilliseconds(1800)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = sine
            });
        }

        private void FadeGlowTo(double edgeOpacity, double bloomOpacity, TimeSpan duration)
        {
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            _softEdgeGlowHost.BeginAnimation(OpacityProperty, new DoubleAnimation(edgeOpacity, new Duration(duration)) { EasingFunction = sine });
            _ambientBloomLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(bloomOpacity, new Duration(duration)) { EasingFunction = sine });
        }

        private void PlaceWindow(IntPtr handle)
        {
            var insertAfter = _insertAfterHandle != IntPtr.Zero && IsWindow(_insertAfterHandle)
                ? _insertAfterHandle
                : HwndTopmost;
            _ = SetWindowPos(
                handle,
                insertAfter,
                _bounds.Left,
                _bounds.Top,
                Math.Max(1, _bounds.Width),
                Math.Max(1, _bounds.Height),
                SwpNoActivate | SwpNoOwnerZOrder);
        }

        private static Grid CreatePureGradientEdgeGlow(double initialOpacity)
        {
            var host = new Grid { Opacity = initialOpacity };
            const double depth = 40;

            host.Children.Add(new Border
            {
                Height = depth,
                VerticalAlignment = VerticalAlignment.Top,
                Background = CreateEdgeGradient(new Point(0.5, 0), new Point(0.5, 1))
            });
            host.Children.Add(new Border
            {
                Height = depth,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = CreateEdgeGradient(new Point(0.5, 1), new Point(0.5, 0))
            });
            host.Children.Add(new Border
            {
                Width = depth,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = CreateEdgeGradient(new Point(0, 0.5), new Point(1, 0.5))
            });
            host.Children.Add(new Border
            {
                Width = depth,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = CreateEdgeGradient(new Point(1, 0.5), new Point(0, 0.5))
            });

            return host;
        }

        private static LinearGradientBrush CreateEdgeGradient(Point start, Point end)
        {
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x52, 0x45, 0xD4, 0x83), 0.0),
                    new(Color.FromArgb(0x20, 0x45, 0xD4, 0x83), 0.42),
                    new(Color.FromArgb(0x00, 0x45, 0xD4, 0x83), 1.0)
                },
                start,
                end);
            brush.Freeze();
            return brush;
        }

        private void SetAccentStroke(Color color)
        {
            var brush = CreateFrozenSolid(color);
            foreach (var shape in _cornerAccents)
            {
                shape.Fill = brush;
            }
        }

        private void AddCornerBracket(Grid parent, HorizontalAlignment hAlign, VerticalAlignment vAlign, int dx, int dy)
        {
            const double length = 24;
            const double thickness = 1.8;
            const double inset = 14;
            var initialBrush = CreateFrozenSolid(Color.FromArgb(0x70, 0x45, 0xD4, 0x83));

            var horizontal = new Rectangle
            {
                Width = length,
                Height = thickness,
                Fill = initialBrush,
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Margin = new Thickness(dx > 0 ? inset : 0, dy > 0 ? inset : 0, dx < 0 ? inset : 0, dy < 0 ? inset : 0)
            };
            var vertical = new Rectangle
            {
                Width = thickness,
                Height = length,
                Fill = initialBrush,
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Margin = new Thickness(dx > 0 ? inset : 0, dy > 0 ? inset : 0, dx < 0 ? inset : 0, dy < 0 ? inset : 0)
            };

            _cornerAccents.Add(horizontal);
            _cornerAccents.Add(vertical);
            parent.Children.Add(horizontal);
            parent.Children.Add(vertical);
        }
    }

    // -------------------------------------------------------------------------
    // 1.5. 黒い全画面シールド（象限ベールのすぐ背面に敷き、十字隙間・角丸隙間・外周のチラつきを完全遮断）
    // -------------------------------------------------------------------------
    private sealed class MonitorBackdropWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly WindowArranger.WindowBounds _bounds;
        private readonly IntPtr _insertAfterHandle;
        private readonly Border _backdropRoot;
        private bool _closed;

        public double CurrentOpacity => _backdropRoot.Opacity;

        public MonitorBackdropWindow(
            WindowArranger.WindowBounds bounds,
            IntPtr insertAfterHandle,
            double initialOpacity)
        {
            _bounds = bounds;
            _insertAfterHandle = insertAfterHandle;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            UseLayoutRounding = false;

            // 角丸なしの完全矩形の深い漆黒（#070A08）で作業領域全面を 1px の隙間もなく覆う。
            _backdropRoot = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x07, 0x0A, 0x08)),
                Opacity = Math.Clamp(initialOpacity, 0.0, 1.0)
            };

            Content = _backdropRoot;
            Closed += (_, _) => _closed = true;
        }

        public void ShowBackdrop()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var exStyle = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);

            PlaceWindow(handle);
            Show();
            PlaceWindow(handle);
            UpdateLayout();
        }

        public Task FadeOpacityAsync(
            double fromOpacity,
            double toOpacity,
            TimeSpan duration,
            TimeSpan beginTime,
            bool briskVanish = false)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            var clampedFrom = Math.Clamp(fromOpacity, 0.0, 1.0);
            var clampedTo = Math.Clamp(toOpacity, 0.0, 1.0);
            if (duration <= TimeSpan.Zero)
            {
                _backdropRoot.BeginAnimation(OpacityProperty, null);
                _backdropRoot.Opacity = clampedTo;
                return Task.CompletedTask;
            }

            _backdropRoot.Opacity = clampedFrom;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            IEasingFunction ease = briskVanish
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new SineEase { EasingMode = EasingMode.EaseInOut };
            var anim = new DoubleAnimation(clampedFrom, clampedTo, new Duration(duration))
            {
                BeginTime = NormalizeBeginTime(beginTime),
                EasingFunction = ease
            };
            // Completed は必ず BeginAnimation より前に登録する。
            anim.Completed += (_, _) =>
            {
                if (!_closed)
                {
                    _backdropRoot.Opacity = clampedTo;
                }

                tcs.TrySetResult(true);
            };
            _backdropRoot.BeginAnimation(OpacityProperty, anim);
            return tcs.Task;
        }

        private void PlaceWindow(IntPtr handle)
        {
            var insertAfter = _insertAfterHandle != IntPtr.Zero && IsWindow(_insertAfterHandle)
                ? _insertAfterHandle
                : HwndTopmost;
            _ = SetWindowPos(
                handle,
                insertAfter,
                _bounds.Left,
                _bounds.Top,
                Math.Max(1, _bounds.Width),
                Math.Max(1, _bounds.Height),
                SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    // -------------------------------------------------------------------------
    // 2. 中央 HUD ウィンドウ（BlurEffect ゼロ・GPU DoubleAnimation 一括同期）
    // -------------------------------------------------------------------------
    private sealed class CenterHudWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly WindowArranger _windowArranger;
        private readonly int _monitorIndex;
        private readonly bool _isClosingMode;
        private readonly Dictionary<string, SlotChipVisual> _chips = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LaunchStageSlotState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _targetSlotNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Grid _cardOuterAura;
        private readonly Border _card;
        private readonly ScaleTransform _cardScale = new(0.97, 0.97);
        private readonly TranslateTransform _cardTranslate = new(0, 6);
        private readonly Ellipse _pulseDot;
        private readonly Ellipse _pulseDotHalo;
        private readonly TextBlock _headlineText;
        private readonly TextBlock _progressCountText;
        private readonly TextBlock _statusText;
        private readonly Border _progressFill;
        private readonly Border _progressTrack;
        private bool _closed;
        private bool _isFadingOut;

        public CenterHudWindow(
            IReadOnlyList<LaunchStageSlotEntry> entries,
            string headline,
            string statusMessage,
            bool isClosingMode,
            WindowArranger windowArranger,
            int monitorIndex)
        {
            _windowArranger = windowArranger;
            _monitorIndex = monitorIndex;
            _isClosingMode = isClosingMode;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;
            IsHitTestVisible = false;
            UseLayoutRounding = false;

            foreach (var entry in entries)
            {
                _states[entry.SlotName] = entry.State;
                if (entry.IsTarget)
                {
                    _targetSlotNames.Add(entry.SlotName);
                }
            }

            var mainStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Width = _targetSlotNames.Count > 1 ? 456 : 376
            };

            // 1 行目: ほのかな光背を持つ LED コアランプ + 見出し + 進捗カウントピル
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dotHaloBrush = new RadialGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(0x90, 0x45, 0xD4, 0x83), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x45, 0xD4, 0x83), 1.0)
                ]
            };
            dotHaloBrush.Freeze();

            _pulseDotHalo = new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = dotHaloBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _pulseDot = new Ellipse
            {
                Width = 8.5,
                Height = 8.5,
                Fill = CreateFrozenSolid(Color.FromRgb(0x45, 0xD4, 0x83)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var dotHost = new Grid
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(-5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dotHost.Children.Add(_pulseDotHalo);
            dotHost.Children.Add(_pulseDot);
            Grid.SetColumn(dotHost, 0);

            _headlineText = new TextBlock
            {
                Text = headline,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateFrozenSolid(Color.FromRgb(0xEE, 0xF4, 0xEF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_headlineText, 1);

            _progressCountText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateFrozenSolid(Color.FromRgb(0x74, 0xE8, 0xA8)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var countPill = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x14, 0x2B, 0x1F)),
                BorderBrush = CreateFrozenSolid(Color.FromArgb(0x7A, 0x45, 0xD4, 0x83)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 2, 9, 2),
                Child = _progressCountText
            };
            Grid.SetColumn(countPill, 2);

            headerGrid.Children.Add(dotHost);
            headerGrid.Children.Add(_headlineText);
            headerGrid.Children.Add(countPill);
            mainStack.Children.Add(headerGrid);

            // 2 行目: 軽量ソフト光漏れ対応のスロット状態チップ群
            var chipsPanel = new Grid
            {
                Margin = new Thickness(0, 14, 0, 0),
                ClipToBounds = false
            };
            var displayedEntries = _targetSlotNames.Count > 1
                ? entries
                : entries.Where(item => item.IsTarget).ToList();

            for (var i = 0; i < displayedEntries.Count; i++)
            {
                chipsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var entry = displayedEntries[i];
                var chip = CreateSlotChip(entry, isSingleWide: displayedEntries.Count == 1);
                if (i > 0)
                {
                    chip.Host.Margin = new Thickness(8, 0, 0, 0);
                }

                Grid.SetColumn(chip.Host, i);
                chipsPanel.Children.Add(chip.Host);
                _chips[entry.SlotName] = chip;
                ApplyChipVisual(chip, entry.State, animate: false);
            }

            mainStack.Children.Add(chipsPanel);

            // 3 行目: ステータス文言 + スリムなネオングリーン進捗バー
            _statusText = new TextBlock
            {
                Text = statusMessage,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = CreateFrozenSolid(Color.FromRgb(0xA7, 0xB5, 0xAC)),
                Margin = new Thickness(0, 14, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            mainStack.Children.Add(_statusText);

            var barBrush = new LinearGradientBrush(
                Color.FromRgb(0x36, 0xB8, 0x6F),
                Color.FromRgb(0x74, 0xE8, 0xA8),
                0);
            barBrush.Freeze();

            _progressFill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Width = 18,
                Background = barBrush
            };
            _progressTrack = new Border
            {
                Height = 3.5,
                CornerRadius = new CornerRadius(2),
                Background = CreateFrozenSolid(Color.FromRgb(0x1E, 0x26, 0x22)),
                Margin = new Thickness(0, 9, 0, 0),
                ClipToBounds = true,
                Child = _progressFill
            };
            mainStack.Children.Add(_progressTrack);

            // HUD カード外周の軽量ソフト光漏れ＋影（BlurEffect / DropShadowEffect 不使用）
            _cardOuterAura = new Grid { Opacity = 0.28, Margin = new Thickness(24) };
            AddSoftRing(_cardOuterAura, outset: 14, cornerRadius: 22, alpha: 0x0C);
            AddSoftRing(_cardOuterAura, outset: 8, cornerRadius: 18, alpha: 0x1A);
            AddSoftRing(_cardOuterAura, outset: 3, cornerRadius: 16, alpha: 0x2C);

            var shadowHost = new Grid { Margin = new Thickness(24) };
            shadowHost.Children.Add(new Border
            {
                Margin = new Thickness(-5, -1, -5, -8),
                CornerRadius = new CornerRadius(19),
                Background = CreateFrozenSolid(Color.FromArgb(0x28, 0x00, 0x00, 0x00))
            });
            shadowHost.Children.Add(new Border
            {
                Margin = new Thickness(-2, 0, -2, -4),
                CornerRadius = new CornerRadius(16),
                Background = CreateFrozenSolid(Color.FromArgb(0x3E, 0x00, 0x00, 0x00))
            });

            var cardFrame = new Border
            {
                Background = CreateFrozenSolid(Color.FromArgb(0xF0, 0x10, 0x13, 0x11)),
                BorderBrush = CreateFrozenSolid(Color.FromArgb(0x80, 0x45, 0xD4, 0x83)),
                BorderThickness = new Thickness(1.1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 18, 24, 18),
                Margin = new Thickness(24),
                Child = mainStack
            };

            var cardRoot = new Grid();
            cardRoot.Children.Add(shadowHost);
            cardRoot.Children.Add(_cardOuterAura);
            cardRoot.Children.Add(cardFrame);

            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(_cardScale);
            transformGroup.Children.Add(_cardTranslate);

            _card = new Border
            {
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transformGroup,
                Child = cardRoot
            };

            Content = _card;
            SourceInitialized += (_, _) => ApplyClickThroughStyles();
            Closed += (_, _) => _closed = true;
            RefreshProgressSummary(animateBar: false);
        }

        public void ShowHud(bool animate)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            ApplyClickThroughStyles();
            PositionCenteredOnMonitor(handle);
            Show();
            PositionCenteredOnMonitor(handle);

            StartPulseAnimation();

            if (!animate)
            {
                _card.Opacity = 1;
                _cardScale.ScaleX = 1;
                _cardScale.ScaleY = 1;
                _cardTranslate.Y = 0;
                return;
            }

            // 初回レイアウト・描画の立ち上がりを 1 フレーム待ってからフェードインし、冒頭のコマ飛びを防ぐ。
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_closed || _isFadingOut)
                {
                    return;
                }

                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var duration = new Duration(HudFadeInDuration);
                _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
                _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
                _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease });
                _cardTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration) { EasingFunction = ease });
            }, DispatcherPriority.Render);
        }

        public void UpdateSlotState(string slotName, LaunchStageSlotState state, string? statusMessage)
        {
            if (_closed)
            {
                return;
            }

            _states[slotName] = state;
            if (_chips.TryGetValue(slotName, out var chip))
            {
                ApplyChipVisual(chip, state, animate: true);
            }

            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                _statusText.Text = statusMessage;
            }

            RefreshProgressSummary(animateBar: true);
        }

        public void UpdateStatusMessage(string statusMessage)
        {
            if (_closed || string.IsNullOrWhiteSpace(statusMessage))
            {
                return;
            }

            _statusText.Text = statusMessage;
        }

        public void ShowReadyEmphasis(string headline, string message)
        {
            if (_closed)
            {
                return;
            }

            _headlineText.Text = headline;
            _statusText.Text = message;
            _pulseDot.Fill = CreateFrozenSolid(Color.FromRgb(0x8E, 0xED, 0xB8));

            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            _cardOuterAura.BeginAnimation(OpacityProperty, new DoubleAnimation(0.64, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = sine });

            var trackWidth = _progressTrack.ActualWidth > 0 ? _progressTrack.ActualWidth : 376;
            _progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(trackWidth, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = sine });
        }

        public void ShowCloseDoneEmphasis(string headline, string message)
        {
            if (_closed)
            {
                return;
            }

            _headlineText.Text = headline;
            _statusText.Text = message;

            // 点滅を止めて静かな完了点灯へ落ち着かせることで、人間が「すべて完了した」と一目で知覚できるようにする。
            _pulseDot.BeginAnimation(OpacityProperty, null);
            _pulseDotHalo.BeginAnimation(OpacityProperty, null);
            _pulseDot.Opacity = 1.0;
            _pulseDotHalo.Opacity = 0.65;
            _pulseDot.Fill = CreateFrozenSolid(Color.FromRgb(0x8E, 0xED, 0xB8));

            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            var auraSettle = new DoubleAnimationUsingKeyFrames();
            auraSettle.KeyFrames.Add(new EasingDoubleKeyFrame(0.48, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))) { EasingFunction = sine });
            auraSettle.KeyFrames.Add(new EasingDoubleKeyFrame(0.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(580))) { EasingFunction = sine });
            _cardOuterAura.BeginAnimation(OpacityProperty, auraSettle);

            var trackWidth = _progressTrack.ActualWidth > 0 ? _progressTrack.ActualWidth : 376;
            _progressFill.BeginAnimation(WidthProperty, new DoubleAnimation(trackWidth, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = sine });
        }

        // 本番の「1 つ 1 つランプがつく」点灯演出（HUD チップ側・BeginTime 一括投入）。
        public void PulseSlotGentleLed(string slotName, TimeSpan beginTime)
        {
            if (_closed || !_chips.TryGetValue(slotName, out var chip))
            {
                return;
            }

            var begin = NormalizeBeginTime(beginTime);
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

            chip.ReadyWash.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.95, new Duration(TimeSpan.FromMilliseconds(360)))
                {
                    BeginTime = begin,
                    EasingFunction = sine
                });

            var outer = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            outer.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = sine });
            outer.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760))) { EasingFunction = sine });
            chip.OuterGlow.BeginAnimation(OpacityProperty, outer);

            var flash = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.50, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))) { EasingFunction = sine });
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760))) { EasingFunction = sine });
            chip.GentleBloom.BeginAnimation(OpacityProperty, flash);

            var badgeGlow = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            badgeGlow.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))) { EasingFunction = sine });
            badgeGlow.KeyFrames.Add(new EasingDoubleKeyFrame(0.80, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760))) { EasingFunction = sine });
            chip.BadgeGlow.BeginAnimation(OpacityProperty, badgeGlow);
        }

        public Task FadeOutAsync(
            TimeSpan duration,
            TimeSpan beginTime,
            bool expandSlightly,
            bool briskVanish = false)
        {
            if (_closed || duration <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            _isFadingOut = true;
            var startOpacity = _card.Opacity > 0.05 ? _card.Opacity : 1.0;
            // BeginTime 待機中に初期ローカル値 0 へ戻って一瞬消えないよう、ローカル値を確定する。
            _card.Opacity = startOpacity;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var begin = NormalizeBeginTime(beginTime);
            IEasingFunction ease = briskVanish
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new SineEase { EasingMode = EasingMode.EaseInOut };
            var d = new Duration(duration);

            var fade = new DoubleAnimation(startOpacity, 0.0, d)
            {
                BeginTime = begin,
                EasingFunction = ease
            };
            // Completed は必ず BeginAnimation より前に登録する。
            fade.Completed += (_, _) =>
            {
                if (!_closed)
                {
                    _card.Opacity = 0.0;
                }

                tcs.TrySetResult(true);
            };

            var targetScale = expandSlightly ? 1.01 : (briskVanish ? 0.985 : 0.96);
            var targetY = expandSlightly ? -3.0 : (briskVanish ? 4.0 : 6.0);

            _card.BeginAnimation(OpacityProperty, fade);
            _cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, targetScale, d) { BeginTime = begin, EasingFunction = ease });
            _cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, targetScale, d) { BeginTime = begin, EasingFunction = ease });
            _cardTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, targetY, d) { BeginTime = begin, EasingFunction = ease });
            return tcs.Task;
        }

        private void StartPulseAnimation()
        {
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            _pulseDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.50, 1.0, new Duration(TimeSpan.FromMilliseconds(1600)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = sine
            });
            _pulseDotHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.24, 0.78, new Duration(TimeSpan.FromMilliseconds(1600)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = sine
            });
        }

        private void RefreshProgressSummary(bool animateBar)
        {
            var total = Math.Max(1, _targetSlotNames.Count);
            var completed = _targetSlotNames.Count(name =>
                _states.TryGetValue(name, out var state)
                && (state is LaunchStageSlotState.Ready or LaunchStageSlotState.Closed or LaunchStageSlotState.Failed));
            var activeBoost = _targetSlotNames.Any(name =>
                _states.TryGetValue(name, out var state)
                && (state is LaunchStageSlotState.Launching or LaunchStageSlotState.Closing))
                ? 0.25
                : 0.0;

            _progressCountText.Text = $"{completed} / {total}";

            var ratio = Math.Clamp((completed + activeBoost) / total, 0.08, 1.0);
            var trackWidth = _progressTrack.ActualWidth > 0
                ? _progressTrack.ActualWidth
                : (_targetSlotNames.Count > 1 ? 456 : 376);
            var targetWidth = Math.Max(16, trackWidth * ratio);

            if (!animateBar)
            {
                _progressFill.Width = targetWidth;
                return;
            }

            _progressFill.BeginAnimation(
                WidthProperty,
                new DoubleAnimation(targetWidth, new Duration(TimeSpan.FromMilliseconds(320)))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private static SlotChipVisual CreateSlotChip(LaunchStageSlotEntry entry, bool isSingleWide)
        {
            var outerGlow = new Grid { Opacity = 0.0, ClipToBounds = false };
            AddSoftRing(outerGlow, outset: 7, cornerRadius: 14, alpha: 0x12);
            AddSoftRing(outerGlow, outset: 4, cornerRadius: 12, alpha: 0x24);
            AddSoftRing(outerGlow, outset: 2, cornerRadius: 10, alpha: 0x3C);

            var baseFrame = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x16, 0x1B, 0x18)),
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x2B, 0x35, 0x2F)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9)
            };

            var readyWash = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x15, 0x33, 0x23)),
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x5E, 0xDB, 0x97)),
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(9),
                Opacity = 0.0
            };

            var gentleBloom = new Border
            {
                Background = CreateFrozenSolid(Color.FromArgb(0x3C, 0x74, 0xE8, 0xA8)),
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x8E, 0xED, 0xB8)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(9),
                Opacity = 0.0
            };

            var badgeGlowBrush = new RadialGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(0x9A, 0x45, 0xD4, 0x83), 0.0),
                    new GradientStop(Color.FromArgb(0x38, 0x45, 0xD4, 0x83), 0.58),
                    new GradientStop(Color.FromArgb(0x00, 0x45, 0xD4, 0x83), 1.0)
                ]
            };
            badgeGlowBrush.Freeze();

            var badgeGlow = new Border
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(-6),
                CornerRadius = new CornerRadius(12),
                Background = badgeGlowBrush,
                Opacity = 0.0
            };

            var badgeText = new TextBlock
            {
                Text = entry.SlotName,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = CreateFrozenSolid(Color.FromRgb(0xEE, 0xF4, 0xEF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = CreateFrozenSolid(Color.FromRgb(0x21, 0x2B, 0x25)),
                Child = badgeText
            };

            var badgeHost = new Grid
            {
                Width = 22,
                Height = 22,
                ClipToBounds = false
            };
            badgeHost.Children.Add(badgeGlow);
            badgeHost.Children.Add(badge);

            var stateLabel = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var topRow = new StackPanel { Orientation = Orientation.Horizontal };
            topRow.Children.Add(badgeHost);
            topRow.Children.Add(stateLabel);

            var subText = new TextBlock
            {
                Text = isSingleWide
                    ? $"{entry.DisplayTitle}  ·  {entry.ApplicationName}"
                    : entry.ApplicationName,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
                Foreground = CreateFrozenSolid(Color.FromRgb(0x9A, 0xA5, 0x9E)),
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var contentStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(9, 8, 9, 8)
            };
            contentStack.Children.Add(topRow);
            contentStack.Children.Add(subText);

            var host = new Grid { ClipToBounds = false };
            host.Children.Add(outerGlow);
            host.Children.Add(baseFrame);
            host.Children.Add(readyWash);
            host.Children.Add(gentleBloom);
            host.Children.Add(contentStack);

            return new SlotChipVisual(host, baseFrame, outerGlow, readyWash, gentleBloom, badge, badgeGlow, badgeText, stateLabel);
        }

        private static void AddSoftRing(Grid parent, double outset, double cornerRadius, byte alpha)
        {
            parent.Children.Add(new Border
            {
                Margin = new Thickness(-outset),
                CornerRadius = new CornerRadius(cornerRadius),
                Background = CreateFrozenSolid(Color.FromArgb(alpha, 0x45, 0xD4, 0x83))
            });
        }

        private void ApplyChipVisual(SlotChipVisual chip, LaunchStageSlotState state, bool animate)
        {
            switch (state)
            {
                case LaunchStageSlotState.AlreadyRunning:
                    chip.BaseFrame.Background = CreateFrozenSolid(Color.FromRgb(0x14, 0x1A, 0x17));
                    chip.BaseFrame.BorderBrush = CreateFrozenSolid(Color.FromRgb(0x2A, 0x3A, 0x31));
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x1E, 0x34, 0x28));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0x9A, 0xD6, 0xB3));
                    chip.StateLabel.Text = "起動済";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x88, 0xA5, 0x93));
                    SetChipGlowImmediate(chip, 0.0, 0.0, 0.0);
                    break;

                case LaunchStageSlotState.Pending:
                    chip.BaseFrame.Background = CreateFrozenSolid(Color.FromRgb(0x16, 0x1B, 0x18));
                    chip.BaseFrame.BorderBrush = CreateFrozenSolid(Color.FromRgb(0x2B, 0x35, 0x2F));
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x23, 0x2B, 0x26));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0xB8, 0xC4, 0xBC));
                    chip.StateLabel.Text = "待機";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x8D, 0x99, 0x91));
                    SetChipGlowImmediate(chip, 0.0, 0.0, 0.0);
                    break;

                case LaunchStageSlotState.Launching:
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x22, 0x5A, 0x3C));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0xF0, 0xFF, 0xF6));
                    chip.StateLabel.Text = "準備中";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x74, 0xE8, 0xA8));
                    StartChipBreathing(chip);
                    break;

                case LaunchStageSlotState.Ready:
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x2F, 0x8F, 0x5B));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0xEC, 0xFB, 0xF2));
                    chip.StateLabel.Text = "準備OK";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x98, 0xDF, 0xB8));
                    if (animate)
                    {
                        PlayChipQuietReady(chip);
                    }
                    else
                    {
                        SetChipGlowImmediate(chip, outerGlow: 0.22, wash: 0.48, badgeGlow: 0.30);
                    }

                    break;

                case LaunchStageSlotState.Closing:
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x24, 0x56, 0x3C));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0xE8, 0xFA, 0xEF));
                    chip.StateLabel.Text = "終了中";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x98, 0xE6, 0xBC));
                    StartChipBreathing(chip);
                    break;

                case LaunchStageSlotState.Closed:
                    chip.BaseFrame.Background = CreateFrozenSolid(Color.FromRgb(0x13, 0x17, 0x15));
                    chip.BaseFrame.BorderBrush = CreateFrozenSolid(Color.FromRgb(0x26, 0x31, 0x2B));
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x1D, 0x26, 0x21));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0x76, 0x8A, 0x7E));
                    chip.StateLabel.Text = "終了済";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0x7E, 0x99, 0x89));
                    if (animate)
                    {
                        PlayChipLedTurnOff(chip);
                    }
                    else
                    {
                        SetChipGlowImmediate(chip, 0.0, 0.0, 0.0);
                    }

                    break;

                case LaunchStageSlotState.Failed:
                    chip.BaseFrame.Background = CreateFrozenSolid(Color.FromRgb(0x24, 0x17, 0x16));
                    chip.BaseFrame.BorderBrush = CreateFrozenSolid(Color.FromRgb(0x7D, 0x42, 0x3B));
                    chip.Badge.Background = CreateFrozenSolid(Color.FromRgb(0x4D, 0x26, 0x22));
                    chip.BadgeText.Foreground = CreateFrozenSolid(Color.FromRgb(0xF2, 0xB7, 0xAF));
                    chip.StateLabel.Text = _isClosingMode ? "確認待" : "未起動";
                    chip.StateLabel.Foreground = CreateFrozenSolid(Color.FromRgb(0xF2, 0xB7, 0xAF));
                    SetChipGlowImmediate(chip, 0.0, 0.0, 0.0);
                    break;
            }
        }

        private static void StartChipBreathing(SlotChipVisual chip)
        {
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(1600));
            chip.OuterGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.08, 0.28, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
            chip.ReadyWash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.14, 0.34, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
            chip.BadgeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.10, 0.32, duration) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
        }

        private static void PlayChipQuietReady(SlotChipVisual chip)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(460));
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            chip.OuterGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.22, duration) { EasingFunction = sine });
            chip.ReadyWash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.48, duration) { EasingFunction = sine });
            chip.BadgeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.30, duration) { EasingFunction = sine });
            chip.GentleBloom.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, duration) { EasingFunction = sine });
        }

        private static void PlayChipLedTurnOff(SlotChipVisual chip)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(380));
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            chip.OuterGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, duration) { EasingFunction = sine });
            chip.ReadyWash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, duration) { EasingFunction = sine });
            chip.BadgeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, duration) { EasingFunction = sine });
            chip.GentleBloom.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, duration) { EasingFunction = sine });
        }

        private static void SetChipGlowImmediate(SlotChipVisual chip, double outerGlow, double wash, double badgeGlow)
        {
            chip.OuterGlow.BeginAnimation(OpacityProperty, null);
            chip.ReadyWash.BeginAnimation(OpacityProperty, null);
            chip.BadgeGlow.BeginAnimation(OpacityProperty, null);
            chip.GentleBloom.BeginAnimation(OpacityProperty, null);
            chip.OuterGlow.Opacity = outerGlow;
            chip.ReadyWash.Opacity = wash;
            chip.BadgeGlow.Opacity = badgeGlow;
            chip.GentleBloom.Opacity = 0.0;
        }

        private void ApplyClickThroughStyles()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var exStyle = GetWindowLong(handle, GwlExStyle);
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExNoActivate | WsExToolWindow);
        }

        private void PositionCenteredOnMonitor(IntPtr handle)
        {
            if (handle == IntPtr.Zero
                || !_windowArranger.TryGetMonitorWorkArea(_monitorIndex, out var work)
                || work.Width <= 0
                || work.Height <= 0)
            {
                return;
            }

            UpdateLayout();
            _card.UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(this);
            var widthPx = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            var heightPx = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            if (widthPx <= 0 || heightPx <= 0)
            {
                return;
            }

            var left = work.Left + ((work.Width - widthPx) / 2);
            var top = work.Top + ((work.Height - heightPx) / 2);
            _ = SetWindowPos(handle, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate);
        }

        private sealed record SlotChipVisual(
            Grid Host,
            Border BaseFrame,
            Grid OuterGlow,
            Border ReadyWash,
            Border GentleBloom,
            Border Badge,
            Border BadgeGlow,
            TextBlock BadgeText,
            TextBlock StateLabel);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
