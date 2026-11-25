using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace TurtleAIQuartetHub.Panel.Services;

// タスクバー上の「このアプリのボタン」の画面上の範囲（物理 px）を UI オートメーションで探す。
// 最小化のときにパネルがそのボタンへ吸い込まれる演出の着地点に使う。
//
// UI オートメーションの問い合わせはエクスプローラーとのプロセス間通信で数十〜百数十 ms かかるため、
// 必ずスレッドプールで実行し、UI スレッドは結果を短い期限つきで待つだけにする。
// 同時に走る問い合わせは 1 本だけにし、最後に見つかった位置を覚えておく（見つからなければ呼び出し側が代わりの位置を使う）。
internal sealed class TaskbarButtonLocator
{
    private const string PrimaryTrayClassName = "Shell_TrayWnd";
    private const string SecondaryTrayClassName = "Shell_SecondaryTrayWnd";
    private const string AppIdPrefix = "Appid: ";
    private const uint MonitorDefaultToNearest = 2;

    private readonly object _gate = new();
    private Task<Rect?>? _pending;
    private Rect? _lastKnown;
    private DateTime _lastFoundAtUtc;
    private bool _hasLoggedFailure;

    public Rect? LastKnown
    {
        get
        {
            lock (_gate)
            {
                return _lastKnown;
            }
        }
    }

    // 直近 maxAge 以内に見つけた位置があれば返す。
    public bool TryGetRecent(TimeSpan maxAge, out Rect rect)
    {
        lock (_gate)
        {
            if (_lastKnown is { } known && DateTime.UtcNow - _lastFoundAtUtc <= maxAge)
            {
                rect = known;
                return true;
            }
        }

        rect = Rect.Empty;
        return false;
    }

    // 位置の問い合わせを始める（すでに走っていればそれを返す）。UI スレッドから呼ぶ。
    public Task<Rect?> RefreshAsync(IntPtr windowHandle, string? windowTitle)
    {
        lock (_gate)
        {
            if (_pending is { IsCompleted: false } running)
            {
                return running;
            }

            var processPath = Environment.ProcessPath;
            var title = windowTitle;
            var task = Task.Run(() => FindSafely(windowHandle, processPath, title));
            _pending = task;
            return task;
        }
    }

    private Rect? FindSafely(IntPtr windowHandle, string? processPath, string? windowTitle)
    {
        try
        {
            var found = Find(windowHandle, processPath, windowTitle);
            if (found is { } rect)
            {
                lock (_gate)
                {
                    _lastKnown = rect;
                    _lastFoundAtUtc = DateTime.UtcNow;
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            // エクスプローラーの再起動中などに失敗することがある。演出は代わりの位置で続けられるので、記録は 1 回だけにする。
            if (!_hasLoggedFailure)
            {
                _hasLoggedFailure = true;
                DiagnosticLog.Write(LogLevel.Warn, $"Taskbar button lookup failed; minimize animation uses the taskbar edge instead. {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }

    private static Rect? Find(IntPtr windowHandle, string? processPath, string? windowTitle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        var windowMonitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        foreach (var tray in EnumerateTrayWindows(windowMonitor))
        {
            var trayElement = AutomationElement.FromHandle(tray);
            if (trayElement is null)
            {
                continue;
            }

            var button = FindOwnButton(trayElement, processPath, windowTitle);
            if (button is null)
            {
                continue;
            }

            var bounds = button.Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                return bounds;
            }
        }

        return null;
    }

    private static AutomationElement? FindOwnButton(AutomationElement trayElement, string? processPath, string? windowTitle)
    {
        // Windows 11: AutomationId が「Appid: <AppUserModelID または実行ファイルのパス>」になっている。
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var byAppId = trayElement.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    AppIdPrefix + processPath,
                    PropertyConditionFlags.IgnoreCase));
            if (byAppId is not null)
            {
                return byAppId;
            }
        }

        // それ以外（Windows 10 など）: ボタンの名前がウィンドウのタイトル（またはタイトル - 実行中の数）になっている。
        var buttons = trayElement.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        foreach (AutomationElement candidate in buttons)
        {
            var current = candidate.Current;
            if (IsOwnTaskbarButton(current.AutomationId, current.Name, processPath, windowTitle))
            {
                return candidate;
            }
        }

        return null;
    }

    // テスト対象: タスクバーのボタンがこのアプリのものかどうか。
    internal static bool IsOwnTaskbarButton(string? automationId, string? name, string? processPath, string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.IsNullOrEmpty(automationId)
            && automationId.StartsWith(AppIdPrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(automationId[AppIdPrefix.Length..].Trim(), processPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(windowTitle) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        // 「Turtle AI Code Quartet Hub - 1 個の実行中ウィンドウ」は一致、「Turtle AI Code Quartet Hub Helper」は不一致。
        return string.Equals(name, windowTitle, StringComparison.Ordinal)
            || name.StartsWith(windowTitle + " - ", StringComparison.Ordinal);
    }

    // パネルと同じディスプレイのタスクバーを先に、残り（主タスクバーなど）を後に返す。
    private static IEnumerable<IntPtr> EnumerateTrayWindows(IntPtr windowMonitor)
    {
        var trays = new List<IntPtr>();
        var primary = FindWindow(PrimaryTrayClassName, null);
        if (primary != IntPtr.Zero)
        {
            trays.Add(primary);
        }

        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, SecondaryTrayClassName, null)) != IntPtr.Zero)
        {
            trays.Add(secondary);
        }

        return trays
            .OrderBy(tray => MonitorFromWindow(tray, MonitorDefaultToNearest) == windowMonitor ? 0 : 1)
            .ToList();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
}
