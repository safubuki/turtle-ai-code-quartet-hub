using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TurtleAIQuartetHub.Panel.Services;

// フォーカス切替（1 面化／4 面戻し）の間、対象ウィンドウの DWM ライブサムネイルで遷移領域を
// 覆い、自前のズームアニメーションを描くオーバーレイ。
//
// 背景: Electron 系（VS Code 等）はリサイズ時に描画が追いつかず、未描画領域の背景（白）が
// 露出してちらつく。これは相手プロセスの描画都合なので外からは直せない。一方 DWM サムネイルは
// コンポジタが「既に描画済みのピクセル」を拡縮合成するだけなので、覆いの中では未描画領域が
// 原理的に見えない。覆いの下で実際の最大化／復元（非同期）と再描画を済ませ、覆いが最終位置に
// 到達して実ピクセルと一致したところで外す。
//
// 設計上の制約:
// - 実ウィンドウの状態遷移（最大化・配置・Z 順）はすべて従来コードのまま。ここは純粋な視覚層で、
//   登録失敗時は何もしない（＝従来の見た目にフォールバック）。
// - 相手プロセスへメッセージを送る API は使わない（ハング耐性方針と同じ）。DWM API と自ウィンドウの
//   SetWindowPos のみ。
// - WPF の AllowsTransparency（レイヤードウィンドウ）上では DWM サムネイルが描画されないため、
//   オーバーレイは不透明の枠なしウィンドウとし、ウィンドウ自体を毎フレーム移動・リサイズする。
public sealed class FocusZoomOverlay
{
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(240);
    // ズーム完了後も覆いを少し保持し、実ウィンドウの再描画が追いつくのを待ってから外す。
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(130);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_VISIBLE = 0x00000008;

    // Win11 の角丸をオーバーレイだけ無効化し、矩形サムネイルとの角のズレを見せない。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    private OverlayWindow? _window;
    private IntPtr _thumbnail;
    private EventHandler? _renderingHandler;
    private Action? _onFinished;
    // Show/Cancel のたびに進める世代トークン。遅延実行（Rendering ハンドラ）が古い世代の
    // 後始末を誤って行わないようにする（FocusNameOverlay と同じ仕組み）。
    private int _generation;

    // from（現在の可視枠）から to（最大化後の作業領域／戻り先セル）へ、対象ウィンドウの
    // ライブサムネイルを拡縮しながら覆う。開始できたときだけ true。
    // keepBelow にパネルのハンドルを渡すと、覆いを常にパネルの直下（＝他のすべての上）に
    // 保ち、パネル自体は隠さない。
    public bool TryPlay(IntPtr sourceWindow, WindowArranger.WindowBounds from, WindowArranger.WindowBounds to, IntPtr keepBelow, Action onFinished)
    {
        CancelActive();

        if (sourceWindow == IntPtr.Zero
            || from.Width <= 0 || from.Height <= 0
            || to.Width <= 0 || to.Height <= 0)
        {
            return false;
        }

        EnsureWindow();
        if (_window is null)
        {
            return false;
        }

        var overlayHandle = new WindowInteropHelper(_window).EnsureHandle();
        if (overlayHandle == IntPtr.Zero)
        {
            return false;
        }

        // 覆いを開始位置（現在のウィンドウと同じ場所）へ置いてから可視化し、
        // 出現そのものが見た目の変化にならないようにする。
        MoveOverlay(overlayHandle, keepBelow, from);
        if (DwmRegisterThumbnail(overlayHandle, sourceWindow, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
        {
            return false;
        }

        _thumbnail = thumbnail;
        UpdateThumbnailDestination(from.Width, from.Height);
        _window.Show();
        MoveOverlay(overlayHandle, keepBelow, from);

        var generation = ++_generation;
        _onFinished = onFinished;
        var stopwatch = Stopwatch.StartNew();

        _renderingHandler = (_, _) =>
        {
            if (generation != _generation)
            {
                return;
            }

            var elapsed = stopwatch.Elapsed;
            if (elapsed >= ZoomDuration + HoldDuration)
            {
                Cleanup();
                return;
            }

            var progress = Math.Clamp(elapsed.TotalMilliseconds / ZoomDuration.TotalMilliseconds, 0.0, 1.0);
            var eased = EaseOutCubic(progress);
            var rect = Lerp(from, to, eased);

            // 毎フレーム Z 順も再主張する。フォーカス切替の既存処理は管理中ウィンドウへ
            // 非同期の TOPMOST バウンスを発行するため、処理タイミングによっては覆いの上へ
            // 一瞬抜けてくる。次フレームで必ず覆い直すことで露出を最大 1 フレームに抑える。
            MoveOverlay(overlayHandle, keepBelow, rect);
            UpdateThumbnailDestination(rect.Width, rect.Height);
        };

        CompositionTarget.Rendering += _renderingHandler;
        return true;
    }

    // 進行中の演出を即座に打ち切り、覆いを外す（次の演出開始・クローズ時に使う）。
    public void CancelActive()
    {
        _generation++;
        Cleanup();
    }

    public void Close()
    {
        CancelActive();
        _window?.Close();
        _window = null;
    }

    private void Cleanup()
    {
        if (_renderingHandler is not null)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            _renderingHandler = null;
        }

        if (_thumbnail != IntPtr.Zero)
        {
            _ = DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = IntPtr.Zero;
        }

        _window?.Hide();

        // 遷移抑止の解除などの後始末は演出の成否にかかわらず必ず 1 回だけ実行する。
        var onFinished = _onFinished;
        _onFinished = null;
        onFinished?.Invoke();
    }

    private void MoveOverlay(IntPtr overlayHandle, IntPtr keepBelow, WindowArranger.WindowBounds rect)
    {
        var insertAfter = keepBelow != IntPtr.Zero ? keepBelow : HWND_TOPMOST;
        if (!SetWindowPos(overlayHandle, insertAfter, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE))
        {
            // パネルが最小化などで挿入先として使えないときは TOPMOST へフォールバック。
            SetWindowPos(overlayHandle, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
        }
    }

    private void UpdateThumbnailDestination(int width, int height)
    {
        if (_thumbnail == IntPtr.Zero)
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE,
            rcDestination = new RECT { Left = 0, Top = 0, Right = width, Bottom = height },
            fVisible = 1
        };
        _ = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
    }

    private static double EaseOutCubic(double t)
    {
        var inverted = 1.0 - t;
        return 1.0 - inverted * inverted * inverted;
    }

    private static WindowArranger.WindowBounds Lerp(WindowArranger.WindowBounds from, WindowArranger.WindowBounds to, double t)
    {
        return new WindowArranger.WindowBounds(
            (int)Math.Round(from.Left + (to.Left - from.Left) * t),
            (int)Math.Round(from.Top + (to.Top - from.Top) * t),
            Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * t)),
            Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * t)));
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = new OverlayWindow();
        _window.SourceInitialized += (_, _) => ApplyOverlayStyles();
    }

    private void ApplyOverlayStyles()
    {
        if (_window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLong(handle, GWL_EXSTYLE, exStyle);

        var cornerPreference = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    // クリックスルー・非アクティブの不透明な枠なしウィンドウ。AllowsTransparency は使わない
    // （レイヤードウィンドウには DWM サムネイルが描画されないため）。サムネイル描画が 1 フレーム
    // 遅れた場合に備え、背景は白ではなくダークにしておく。
    private sealed class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // サイズ・位置は物理ピクセルで SetWindowPos が管理する。WPF のレイアウトには任せない。
            Width = 1;
            Height = 1;
            Left = -32000;
            Top = -32000;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
