using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TurtleAIQuartetHub.Panel.Services;

// 単体起動・アプリ切替・控え復帰・Launch Quartet 一括起動のすべてで共有するステルス起動セッション。
//
// 役割:
//   1. SetWinEventHook で対象アプリの新規トップレベルウィンドウが生まれた瞬間（CREATE/SHOW）を捕まえ、
//      白画面やバラバラの初期位置が画面に見える前に DWMWA_CLOAK と遷移無効化を先制適用する。
//   2. 起動前から存在していた既存ウィンドウ（knownHandles）やモーダルダイアログ・ツールウィンドウには
//      一切触れない。
//   3. スロットに正式採用（Adopt）されたウィンドウは、全対象の裏配置と位置安定化が整うまでクロークを
//      維持し、準備完了の合図で ReleaseAllAdopted() により一斉解放する。
//   4. 採用されなかった先制クローク窓は ReleaseUnadopted() で即座に解放し、さらにウォッチドッグと
//      Dispose() により、例外やタイムアウトが起きても透明なウィンドウを残さないことを保証する。
public sealed class LaunchWindowCloakSession : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjectIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsChild = 0x40000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly object _sync = new();
    private readonly HashSet<IntPtr> _knownHandles = [];
    private readonly HashSet<IntPtr> _speculativeCloaked = [];
    private readonly HashSet<IntPtr> _adoptedCloaked = [];
    private readonly HashSet<string> _targetProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string?> _processNameCache = [];
    private readonly Func<IntPtr, bool, bool> _setCloaked;
    private readonly Func<IntPtr, bool, bool> _setTransitionsDisabled;
    private readonly Func<IntPtr, bool> _isEligibleWindow;
    private readonly Func<IntPtr, string?> _resolveProcessName;
    private readonly CancellationTokenSource _failsafeCts = new();
    private readonly WinEventDelegate? _winEventCallback;
    private IntPtr _winEventHook;
    private TaskCompletionSource<bool> _candidateSignal = CreateSignalSource();
    private bool _disposed;

    public LaunchWindowCloakSession(TimeSpan failsafeTimeout)
        : this(
            failsafeTimeout,
            enableWinEventHook: true,
            WindowArranger.SetCloaked,
            WindowArranger.SetTransitionsDisabled,
            IsEligibleTopLevelAppWindow,
            ResolveWindowProcessName)
    {
    }

    internal LaunchWindowCloakSession(
        TimeSpan failsafeTimeout,
        bool enableWinEventHook,
        Func<IntPtr, bool, bool> setCloaked,
        Func<IntPtr, bool, bool> setTransitionsDisabled,
        Func<IntPtr, bool> isEligibleWindow,
        Func<IntPtr, string?> resolveProcessName)
    {
        _setCloaked = setCloaked;
        _setTransitionsDisabled = setTransitionsDisabled;
        _isEligibleWindow = isEligibleWindow;
        _resolveProcessName = resolveProcessName;

        if (enableWinEventHook)
        {
            _winEventCallback = OnWinEvent;
            _winEventHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectNameChange,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
            if (_winEventHook == IntPtr.Zero)
            {
                DiagnosticLog.Write(LogLevel.Warn, "LaunchWindowCloakSession could not register WinEvent hook.");
            }
        }

        if (failsafeTimeout > TimeSpan.Zero)
        {
            _ = RunFailsafeAsync(failsafeTimeout, _failsafeCts.Token);
        }
    }

    public int AdoptedCount
    {
        get
        {
            lock (_sync)
            {
                return _adoptedCloaked.Count;
            }
        }
    }

    public int SpeculativeCount
    {
        get
        {
            lock (_sync)
            {
                return _speculativeCloaked.Count;
            }
        }
    }

    public void RegisterKnownHandles(IEnumerable<IntPtr> handles)
    {
        lock (_sync)
        {
            foreach (var handle in handles)
            {
                if (handle != IntPtr.Zero)
                {
                    _knownHandles.Add(handle);
                }
            }
        }
    }

    public void AddTargetProcessNames(IEnumerable<string> processNames)
    {
        lock (_sync)
        {
            foreach (var name in processNames)
            {
                var normalized = NormalizeProcessName(name);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _targetProcessNames.Add(normalized);
                }
            }
        }
    }

    public IReadOnlyCollection<IntPtr> GetAdoptedHandles()
    {
        lock (_sync)
        {
            return _adoptedCloaked.ToArray();
        }
    }

    // テストからも直接呼び出せる先制クローク判定。WinEvent コールバック本体と同一ロジックを共有する。
    internal bool TrySpeculativeCloak(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed
                || _targetProcessNames.Count == 0
                || _knownHandles.Contains(windowHandle)
                || _adoptedCloaked.Contains(windowHandle))
            {
                return false;
            }

            if (_speculativeCloaked.Contains(windowHandle))
            {
                SignalCandidateLocked();
                return true;
            }
        }

        if (!_isEligibleWindow(windowHandle))
        {
            return false;
        }

        var processName = GetCachedProcessName(windowHandle);
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed
                || !_targetProcessNames.Contains(processName)
                || _knownHandles.Contains(windowHandle)
                || _adoptedCloaked.Contains(windowHandle))
            {
                return false;
            }

            if (_speculativeCloaked.Contains(windowHandle))
            {
                SignalCandidateLocked();
                return true;
            }

            var cloaked = _setCloaked(windowHandle, true);
            if (cloaked)
            {
                _ = _setTransitionsDisabled(windowHandle, true);
                _speculativeCloaked.Add(windowHandle);
            }

            SignalCandidateLocked();
            return cloaked;
        }
    }

    // スロットへの割り当てが確定したウィンドウをセッション管理下へ引き継ぐ。
    // すでに先制クローク済みならそのまま維持し、未クロークならこの瞬間に確実にクロークする。
    public bool Adopt(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            _knownHandles.Add(windowHandle);
            if (_adoptedCloaked.Contains(windowHandle))
            {
                return true;
            }

            if (_speculativeCloaked.Remove(windowHandle))
            {
                _adoptedCloaked.Add(windowHandle);
                return true;
            }

            var cloaked = _setCloaked(windowHandle, true);
            if (cloaked)
            {
                _ = _setTransitionsDisabled(windowHandle, true);
                _adoptedCloaked.Add(windowHandle);
            }

            return cloaked;
        }
    }

    // 採用されなかった先制クローク窓（通知や別ウィンドウ等）を即座に元へ戻す。
    public void ReleaseUnadopted()
    {
        List<IntPtr> toRelease;
        lock (_sync)
        {
            if (_speculativeCloaked.Count == 0)
            {
                return;
            }

            toRelease = _speculativeCloaked.ToList();
            _speculativeCloaked.Clear();
        }

        ReleaseHandles(toRelease);
    }

    // 採用済み・先制クローク済みの全ウィンドウを一斉に可視化（クローク解除）する。
    public IReadOnlyList<IntPtr> ReleaseAllAdopted()
    {
        List<IntPtr> adopted;
        List<IntPtr> unadopted;
        lock (_sync)
        {
            adopted = _adoptedCloaked.ToList();
            _adoptedCloaked.Clear();

            unadopted = _speculativeCloaked.ToList();
            _speculativeCloaked.Clear();
        }

        ReleaseHandles(adopted);
        ReleaseHandles(unadopted);
        return adopted;
    }

    // ウェーブ演出で 1 象限ずつ順番に開くときに、指定した 1 ウィンドウだけをクローク解除する。
    public bool ReleaseAdopted(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        bool removed;
        lock (_sync)
        {
            removed = _adoptedCloaked.Remove(windowHandle) | _speculativeCloaked.Remove(windowHandle);
        }

        if (!removed)
        {
            return false;
        }

        ReleaseHandles([windowHandle]);
        return true;
    }

    // 新規ウィンドウ生成・タイトル変更イベントか、指定待機時間のどちらか早い方まで待つ。
    public async Task WaitForCandidateSignalAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        if (maxWait <= TimeSpan.Zero)
        {
            return;
        }

        Task<bool> signalTask;
        lock (_sync)
        {
            signalTask = _candidateSignal.Task;
        }

        var delayTask = Task.Delay(maxWait, cancellationToken);
        _ = await Task.WhenAny(signalTask, delayTask);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        IntPtr hookToRelease;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hookToRelease = _winEventHook;
            _winEventHook = IntPtr.Zero;
            SignalCandidateLocked();
        }

        try
        {
            _failsafeCts.Cancel();
            _failsafeCts.Dispose();
        }
        catch
        {
            // Ignore cancellation cleanup failures.
        }

        if (hookToRelease != IntPtr.Zero)
        {
            _ = UnhookWinEvent(hookToRelease);
            GC.KeepAlive(_winEventCallback);
        }

        // 万一 ReleaseAllAdopted を通らずにセッションが破棄された場合も、必ず全クロークを解除する。
        _ = ReleaseAllAdopted();
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (idObject != ObjectIdWindow || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType != EventObjectCreate
            && eventType != EventObjectShow
            && eventType != EventObjectNameChange)
        {
            return;
        }

        try
        {
            _ = TrySpeculativeCloak(hwnd);
        }
        catch
        {
            // WinEvent callback must never throw across native boundary.
        }
    }

    private async Task RunFailsafeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var released = ReleaseAllAdopted();
        if (released.Count > 0)
        {
            DiagnosticLog.Write(
                LogLevel.Warn,
                $"LaunchWindowCloakSession failsafe released {released.Count} cloaked window(s) after {timeout.TotalSeconds:0}s.");
        }
    }

    private void ReleaseHandles(IEnumerable<IntPtr> handles)
    {
        foreach (var handle in handles)
        {
            try
            {
                _ = _setTransitionsDisabled(handle, false);
                _ = _setCloaked(handle, false);
            }
            catch
            {
                // Best effort release for each window.
            }
        }
    }

    private string? GetCachedProcessName(IntPtr windowHandle)
    {
        if (_resolveProcessName != ResolveWindowProcessName)
        {
            return NormalizeProcessName(_resolveProcessName(windowHandle));
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        lock (_sync)
        {
            if (_processNameCache.TryGetValue(processId, out var cached))
            {
                return cached;
            }
        }

        var resolved = NormalizeProcessName(ResolveProcessNameById(processId));
        lock (_sync)
        {
            if (_processNameCache.Count > 256)
            {
                _processNameCache.Clear();
            }

            _processNameCache[processId] = resolved;
        }

        return resolved;
    }

    private void SignalCandidateLocked()
    {
        var current = _candidateSignal;
        _candidateSignal = CreateSignalSource();
        current.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateSignalSource()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsEligibleTopLevelAppWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (GetAncestor(windowHandle, GaRoot) != windowHandle)
        {
            return false;
        }

        var style = GetWindowLong(windowHandle, GwlStyle);
        if ((style & WsChild) != 0)
        {
            return false;
        }

        var exStyle = GetWindowLong(windowHandle, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        // 確認ダイアログや認証プロンプトなどの所有ウィンドウは絶対にクロークしない。
        if (GetWindow(windowHandle, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        return true;
    }

    private static string? ResolveWindowProcessName(IntPtr windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == 0 ? null : ResolveProcessNameById(processId);
    }

    private static string? ResolveProcessNameById(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(name.Trim()).ToLowerInvariant();
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
