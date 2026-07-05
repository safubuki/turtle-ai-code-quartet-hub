using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurtleAIQuartetHub.Panel.Services;

// フォーカス切替（1 面化／4 面戻し）の間、遷移領域を覆って自前のズームアニメーションを描く
// オーバーレイ。Electron(VS Code) はリサイズ時に描画が追いつかず背景が露出してちらつくため、
// 実際の最大化／復元は覆いの下で済ませ、覆いには常に「完成済みのピクセル」だけを見せる。
//
// なめらかさの要（v1 の反省点）:
// - 覆いウィンドウ自体は再生開始時に 1 度だけ対象ディスプレイの作業領域全体へ配置し、
//   アニメーション中は絶対に動かさない・リサイズしない。ウィンドウのジオメトリ変更は
//   DWM の合成フレームと同期せず、サーフェス再確保も伴うため、毎フレーム行うと必ずガタつく。
// - 動かすのは「ウィンドウの中身」だけ。拡大はスクリーンショットの切り抜きを
//   RenderTransform（GPU 合成・vsync 同期）でズームし、縮小は DWM ライブサムネイルの
//   出力先矩形（コンポジタ側で合成）だけを更新する。
//
// 拡大（1 面化）: 開始時に作業領域を 1 枚キャプチャし、全景を背景に敷いた上で対象ウィンドウ
// 部分の切り抜きを拡大する。ピクセルが完全に静的なので、覆いの中に未描画（白）が映り込む
// 余地がない。覆いの下では実ウィンドウが最大化・再描画を終えており、外した瞬間は
// 「拡大しきった切り抜き」と「実ウィンドウ」がほぼ同一ピクセルで入れ替わる。
//
// 縮小（4 面戻し）: 縮小する対象はライブサムネイルで見せる（縮小方向のリサイズは既存ピクセルが
// 維持されるため白が出ない）。背面には他タイルのライブサムネイルを静止配置し、覆いを外した
// ときの絵と一致させる。
//
// 設計上の制約:
// - 実ウィンドウの状態遷移（最大化・配置・Z 順）はすべて従来コードのまま。ここは純粋な視覚層で、
//   開始失敗時は何もしない（＝従来の見た目にフォールバック）。
// - 相手プロセスへメッセージを送る API は使わない（ハング耐性方針と同じ）。画面 DC の BitBlt・
//   DWM API・自ウィンドウ操作のみ。
// - WPF の AllowsTransparency（レイヤードウィンドウ）上では DWM サムネイルが描画されないため、
//   覆いは不透明の枠なしウィンドウとする。
public sealed class FocusZoomOverlay
{
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(260);
    // ズーム完了後も覆いを少し保持し、実ウィンドウの再描画が追いつくのを待ってから外す。
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(140);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    private const uint DWM_TNP_OPACITY = 0x00000004;
    private const uint DWM_TNP_VISIBLE = 0x00000008;
    private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    private const uint SRCCOPY = 0x00CC0020;

    // Win11 の角丸をオーバーレイだけ無効化し、矩形コンテンツとの角のズレを見せない。
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    // 縮小演出の背面に静止表示する管理中ウィンドウ（他タイル）。
    public readonly record struct BackdropWindow(IntPtr Handle, WindowArranger.WindowBounds Bounds);

    private OverlayWindow? _window;
    private Image? _backdropImage;
    private Image? _zoomImage;
    private ScaleTransform? _zoomScale;
    private TranslateTransform? _zoomTranslate;
    private readonly List<IntPtr> _thumbnails = [];
    private IntPtr _targetThumbnail;
    private EventHandler? _renderingHandler;
    private Action? _onFinished;
    private TaskCompletionSource<bool>? _coverPresented;
    // Play/Cancel のたびに進める世代トークン。遅延実行（Rendering ハンドラ）が古い世代の
    // 後始末を誤って行わないようにする（FocusNameOverlay と同じ仕組み）。
    private int _generation;

    // 拡大（1 面化）: from（現在のタイル可視枠）→ to（作業領域）へ、スクリーンショットの
    // 切り抜きをズームさせる。開始できたときだけ true。
    // keepBelow にパネルのハンドルを渡すと、覆いを常にパネルの直下（＝他のすべての上）に保つ。
    public bool TryPlayGrow(
        IntPtr sourceWindow,
        WindowArranger.WindowBounds from,
        WindowArranger.WindowBounds hostArea,
        IntPtr keepBelow,
        Action onFinished)
    {
        CancelActive();

        if (sourceWindow == IntPtr.Zero || !IsValidRect(from) || !IsValidRect(hostArea))
        {
            return false;
        }

        var screenshot = CaptureScreen(hostArea);
        if (screenshot is null)
        {
            return false;
        }

        if (!PrepareHost(hostArea, keepBelow, out var overlayHandle))
        {
            return false;
        }

        _backdropImage!.Source = screenshot;

        // 対象ウィンドウ部分の切り抜き。作業領域から一部はみ出していても落ちないよう丸める。
        var crop = ClampToBitmap(from, hostArea, screenshot);
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            Cleanup();
            return false;
        }

        var cropped = new CroppedBitmap(screenshot, crop);
        cropped.Freeze();
        var dpi = VisualTreeHelper.GetDpi(_window!);
        _zoomImage!.Source = cropped;
        _zoomImage.Width = from.Width / dpi.DpiScaleX;
        _zoomImage.Height = from.Height / dpi.DpiScaleY;
        _zoomImage.Visibility = Visibility.Visible;
        ApplyZoomImageRect(from, from, hostArea, dpi);

        // 静止画の切り抜きだけで最後まで引き伸ばすと、終点が「タイルの絵を約 2 倍に拡大した
        // ボケた画面」になってしまい、覆いを外す瞬間に実ウィンドウ（等倍・再レイアウト済み）へ
        // 大きくポップする。そこで同じ矩形にライブサムネイルを重ね、進行に合わせて
        // 透明→不透明へクロスフェードする。序盤は静止画が実ウィンドウの未描画（白）を隠し、
        // 終盤はライブの「本物の最大化後の絵」が等倍で映るため、覆いの解除はシームレスになる。
        // 再描画が遅れてもライブ側がそのまま見えるので、解除後に突然ちらつきが現れることもない。
        if (DwmRegisterThumbnail(overlayHandle, sourceWindow, out var liveThumbnail) == 0 && liveThumbnail != IntPtr.Zero)
        {
            _thumbnails.Add(liveThumbnail);
            _targetThumbnail = liveThumbnail;
            UpdateThumbnailDestination(liveThumbnail, ToHostRect(from, hostArea), opacity: 0);
        }

        BeginAnimation(
            overlayHandle,
            keepBelow,
            onFinished,
            (rect, progress) =>
            {
                ApplyZoomImageRect(rect, from, hostArea, dpi);
                if (_targetThumbnail != IntPtr.Zero)
                {
                    // ライブへのフェードは時間ベースで後半に寄せる。序盤〜中盤（実ウィンドウが
                    // 最大化・再描画中で最も汚い時間帯）は静止画を主役に保ち、45% 経過から
                    // なめらかに立ち上げて 95% でライブへ完全に切り替える。イージング後の値を
                    // 使うと ease-out は序盤で一気に進むため、時間（線形進行）で判定すること。
                    var ramp = Math.Clamp((progress - 0.45) / 0.5, 0.0, 1.0);
                    var opacity = (byte)Math.Clamp((int)Math.Round(ramp * ramp * 255.0), 0, 255);
                    UpdateThumbnailDestination(_targetThumbnail, ToHostRect(rect, hostArea), opacity);
                }
            },
            from,
            hostArea);
        return true;
    }

    // 縮小（4 面戻し）: from（作業領域いっぱい）→ to（戻り先セル）へ、対象のライブサムネイルを
    // 縮める。backdrops には他タイルを渡し、覆いの背面に静止サムネイルとして敷いて、
    // 覆いを外したときの絵と一致させる。
    public bool TryPlayShrink(
        IntPtr sourceWindow,
        WindowArranger.WindowBounds from,
        WindowArranger.WindowBounds to,
        WindowArranger.WindowBounds hostArea,
        IReadOnlyList<BackdropWindow> backdrops,
        IntPtr keepBelow,
        Action onFinished)
    {
        CancelActive();

        if (sourceWindow == IntPtr.Zero || !IsValidRect(from) || !IsValidRect(to) || !IsValidRect(hostArea))
        {
            return false;
        }

        if (!PrepareHost(hostArea, keepBelow, out var overlayHandle))
        {
            return false;
        }

        // 縮小時の背景にスクリーンショットを敷いてはいけない。この瞬間の画面は
        // 「最大化表示そのもの」なので、敷くと古い 1 面表示が覆いの背景として全画面に残り、
        // 縮小が終わってもしばらく残像のように見えてしまう。背景はダーク（タイル間の
        // 6px ギャップ等にだけ見える）とし、タイルはライブサムネイルで上に敷く。
        _backdropImage!.Source = null;

        // 登録順がそのまま重なり順（後勝ち）。他タイル → 対象の順で、対象を最前面にする。
        foreach (var backdrop in backdrops)
        {
            if (backdrop.Handle == IntPtr.Zero || !IsValidRect(backdrop.Bounds))
            {
                continue;
            }

            if (DwmRegisterThumbnail(overlayHandle, backdrop.Handle, out var thumbnail) == 0 && thumbnail != IntPtr.Zero)
            {
                _thumbnails.Add(thumbnail);
                UpdateThumbnailDestination(thumbnail, ToHostRect(backdrop.Bounds, hostArea));
            }
        }

        if (DwmRegisterThumbnail(overlayHandle, sourceWindow, out var targetThumbnail) != 0 || targetThumbnail == IntPtr.Zero)
        {
            Cleanup();
            return false;
        }

        _thumbnails.Add(targetThumbnail);
        _targetThumbnail = targetThumbnail;
        UpdateThumbnailDestination(targetThumbnail, ToHostRect(from, hostArea));

        BeginAnimation(
            overlayHandle,
            keepBelow,
            onFinished,
            (rect, _) => UpdateThumbnailDestination(_targetThumbnail, ToHostRect(rect, hostArea)),
            from,
            to);
        return true;
    }

    // 覆いが実際に画面へ合成されるまで待つ。呼び出し側は覆い開始後、実ウィンドウの
    // 最大化・整列など「画面が大きく変わる操作」をこれを待ってから発行すること。
    // Show() 直後はまだ覆いが描画されておらず、その隙間に実ウィンドウの遷移
    // （未描画サーフェス＝背面が透けて見える）が丸見えになってしまう。
    // Rendering 2 tick ＝ 最初のフレームが確実に提示された後。キャンセル時も解放される。
    public Task WaitForCoverPresentedAsync()
    {
        return _coverPresented?.Task ?? Task.CompletedTask;
    }

    // 起動直後の初回再生で WPF ウィンドウ生成のもたつきが出ないよう、ハンドルだけ先に作っておく。
    public void Warmup()
    {
        EnsureWindow();
        if (_window is not null)
        {
            _ = new WindowInteropHelper(_window).EnsureHandle();
        }
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

    // 覆いウィンドウを作業領域全体へ 1 度だけ配置して可視化する。以後アニメーション中に
    // ジオメトリは変更しない（ここがなめらかさの生命線）。
    private bool PrepareHost(WindowArranger.WindowBounds hostArea, IntPtr keepBelow, out IntPtr overlayHandle)
    {
        EnsureWindow();
        overlayHandle = _window is null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();
        if (overlayHandle == IntPtr.Zero)
        {
            return false;
        }

        PlaceHost(overlayHandle, keepBelow, hostArea);

        // WPF 側の DIP プロパティも実配置に合わせておく。初期値（1x1・画面外）のままだと、
        // WPF が Show やレイアウトのタイミングで自前の配置を再適用したとき覆いが潰れてしまう。
        var dpi = VisualTreeHelper.GetDpi(_window!);
        _window!.Left = hostArea.Left / dpi.DpiScaleX;
        _window.Top = hostArea.Top / dpi.DpiScaleY;
        _window.Width = hostArea.Width / dpi.DpiScaleX;
        _window.Height = hostArea.Height / dpi.DpiScaleY;

        _window.Show();
        // Show 直後に WPF が自前の配置（DIP プロパティ由来）で上書きすることがあるため据え直し、
        // 最初の合成フレームまでにコンテンツのレイアウトも済ませておく。
        PlaceHost(overlayHandle, keepBelow, hostArea);
        _window.UpdateLayout();
        return true;
    }

    private void BeginAnimation(
        IntPtr overlayHandle,
        IntPtr keepBelow,
        Action onFinished,
        Action<WindowArranger.WindowBounds, double> applyFrame,
        WindowArranger.WindowBounds from,
        WindowArranger.WindowBounds to)
    {
        var generation = ++_generation;
        _onFinished = onFinished;
        _coverPresented = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderedTicks = 0;
        var stopwatch = Stopwatch.StartNew();

        _renderingHandler = (_, _) =>
        {
            if (generation != _generation)
            {
                return;
            }

            // Rendering はフレーム準備時に発火する。1 tick 目のフレームが次の vsync で
            // 画面に載るため、2 tick 目＝「覆いは確実に見えている」の合図。
            if (renderedTicks < 2 && ++renderedTicks == 2)
            {
                _coverPresented?.TrySetResult(true);
            }

            var elapsed = stopwatch.Elapsed;
            if (elapsed >= ZoomDuration + HoldDuration)
            {
                Cleanup();
                return;
            }

            // 矩形はイージング済みの位置で動かし、applyFrame へは時間（線形進行）を渡す。
            // クロスフェードなど「時間帯」で制御したい効果が ease-out の急伸に引きずられないようにする。
            var progress = Math.Clamp(elapsed.TotalMilliseconds / ZoomDuration.TotalMilliseconds, 0.0, 1.0);
            applyFrame(Lerp(from, to, EaseOutQuart(progress)), progress);

            // 覆いのジオメトリは触らず、Z 順だけ毎フレーム再主張する。フォーカス切替の既存処理は
            // 管理中ウィンドウへ非同期の TOPMOST バウンスを発行するため、タイミングによっては
            // 覆いの上へ一瞬抜けてくる。次フレームで必ず覆い直し、露出を最大 1 フレームに抑える。
            ReassertZOrder(overlayHandle, keepBelow);
        };

        CompositionTarget.Rendering += _renderingHandler;
    }

    private void ApplyZoomImageRect(
        WindowArranger.WindowBounds rect,
        WindowArranger.WindowBounds from,
        WindowArranger.WindowBounds hostArea,
        DpiScale dpi)
    {
        if (_zoomScale is null || _zoomTranslate is null)
        {
            return;
        }

        // 切り抜き画像は (0,0) に from サイズ（DIP）で置いてあり、スケール→平行移動の合成で
        // 目的矩形へ写す。テクスチャ変形だけなのでレイアウトは走らず、GPU 合成で滑らかに動く。
        _zoomScale.ScaleX = rect.Width / (double)from.Width;
        _zoomScale.ScaleY = rect.Height / (double)from.Height;
        _zoomTranslate.X = (rect.Left - hostArea.Left) / dpi.DpiScaleX;
        _zoomTranslate.Y = (rect.Top - hostArea.Top) / dpi.DpiScaleY;
    }

    private void Cleanup()
    {
        if (_renderingHandler is not null)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            _renderingHandler = null;
        }

        // 必ずウィンドウを隠してからサムネイルを解除する。逆順にすると「サムネイルだけ消えて
        // 覆い（古い背景画像）が 1 フレーム全画面に見える」瞬間ができ、解除時のちらつきになる。
        _window?.Hide();

        foreach (var thumbnail in _thumbnails)
        {
            _ = DwmUnregisterThumbnail(thumbnail);
        }

        _thumbnails.Clear();
        _targetThumbnail = IntPtr.Zero;

        // スクリーンショット（数十 MB になり得る）を覆いの非表示中まで保持しない。
        if (_backdropImage is not null)
        {
            _backdropImage.Source = null;
        }

        if (_zoomImage is not null)
        {
            _zoomImage.Source = null;
            _zoomImage.Visibility = Visibility.Collapsed;
        }

        // 覆い提示待ちの呼び出し側をキャンセル時も解放する（永久待ちの防止）。
        _coverPresented?.TrySetResult(true);

        // 遷移抑止の解除などの後始末は演出の成否にかかわらず必ず 1 回だけ実行する。
        var onFinished = _onFinished;
        _onFinished = null;
        onFinished?.Invoke();
    }

    private static void PlaceHost(IntPtr overlayHandle, IntPtr keepBelow, WindowArranger.WindowBounds hostArea)
    {
        var insertAfter = keepBelow != IntPtr.Zero ? keepBelow : HWND_TOPMOST;
        if (!SetWindowPos(overlayHandle, insertAfter, hostArea.Left, hostArea.Top, hostArea.Width, hostArea.Height, SWP_NOACTIVATE))
        {
            SetWindowPos(overlayHandle, HWND_TOPMOST, hostArea.Left, hostArea.Top, hostArea.Width, hostArea.Height, SWP_NOACTIVATE);
        }
    }

    private static void ReassertZOrder(IntPtr overlayHandle, IntPtr keepBelow)
    {
        var insertAfter = keepBelow != IntPtr.Zero ? keepBelow : HWND_TOPMOST;
        if (!SetWindowPos(overlayHandle, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
        {
            SetWindowPos(overlayHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private void UpdateThumbnailDestination(IntPtr thumbnail, RECT destination)
    {
        UpdateThumbnailDestination(thumbnail, destination, opacity: 255);
    }

    private void UpdateThumbnailDestination(IntPtr thumbnail, RECT destination, byte opacity)
    {
        if (thumbnail == IntPtr.Zero)
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destination,
            opacity = opacity,
            fVisible = 1,
            // クライアント領域のみを描く。ウィンドウ全体を使うと、最大化ウィンドウでは画面外へ
            // はみ出した不可視枠まで含めて縮められ、実ウィンドウと数 px ずれて解除時に
            // 「わずかに動く」ちらつきになる。VS Code はタイトルバーもクライアント描画なので
            // 見た目の欠けは生じない。
            fSourceClientAreaOnly = 1
        };
        _ = DwmUpdateThumbnailProperties(thumbnail, ref properties);
    }

    private static RECT ToHostRect(WindowArranger.WindowBounds bounds, WindowArranger.WindowBounds hostArea)
    {
        return new RECT
        {
            Left = bounds.Left - hostArea.Left,
            Top = bounds.Top - hostArea.Top,
            Right = bounds.Left - hostArea.Left + bounds.Width,
            Bottom = bounds.Top - hostArea.Top + bounds.Height
        };
    }

    private static Int32Rect ClampToBitmap(
        WindowArranger.WindowBounds rect,
        WindowArranger.WindowBounds hostArea,
        BitmapSource bitmap)
    {
        var left = Math.Clamp(rect.Left - hostArea.Left, 0, Math.Max(0, bitmap.PixelWidth - 1));
        var top = Math.Clamp(rect.Top - hostArea.Top, 0, Math.Max(0, bitmap.PixelHeight - 1));
        var width = Math.Clamp(rect.Width, 1, bitmap.PixelWidth - left);
        var height = Math.Clamp(rect.Height, 1, bitmap.PixelHeight - top);
        return new Int32Rect(left, top, width, height);
    }

    private static bool IsValidRect(WindowArranger.WindowBounds bounds)
    {
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static double EaseOutQuart(double t)
    {
        var inverted = 1.0 - t;
        inverted *= inverted;
        return 1.0 - inverted * inverted;
    }

    private static WindowArranger.WindowBounds Lerp(WindowArranger.WindowBounds from, WindowArranger.WindowBounds to, double t)
    {
        return new WindowArranger.WindowBounds(
            (int)Math.Round(from.Left + (to.Left - from.Left) * t),
            (int)Math.Round(from.Top + (to.Top - from.Top) * t),
            Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * t)),
            Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * t)));
    }

    // 画面 DC からの BitBlt。対象プロセスへは一切触れない（ハング耐性）。
    // CAPTUREBLT は使わない: レイヤードウィンドウ（フォーカス名カード等）を写し込まないほうが、
    // 覆い越しに見える実物のカードと二重にならず自然になる。
    private static BitmapSource? CaptureScreen(WindowArranger.WindowBounds area)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmap = CreateCompatibleBitmap(screenDc, area.Width, area.Height);
                if (bitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var previous = SelectObject(memoryDc, bitmap);
                    var copied = BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.Left, area.Top, SRCCOPY);
                    SelectObject(memoryDc, previous);
                    if (!copied)
                    {
                        return null;
                    }

                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        bitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            finally
            {
                DeleteDC(memoryDc);
            }
        }
        catch
        {
            // キャプチャ失敗は演出をあきらめるだけで、機能には影響させない。
            return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _backdropImage = new Image
        {
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(_backdropImage, BitmapScalingMode.NearestNeighbor);

        _zoomImage = new Image
        {
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(_zoomImage, BitmapScalingMode.Linear);
        _zoomScale = new ScaleTransform(1.0, 1.0);
        _zoomTranslate = new TranslateTransform(0.0, 0.0);
        _zoomImage.RenderTransform = new TransformGroup
        {
            Children = { _zoomScale, _zoomTranslate }
        };

        var root = new Grid();
        root.Children.Add(_backdropImage);
        root.Children.Add(_zoomImage);

        _window = new OverlayWindow
        {
            Content = root
        };
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
    // （レイヤードウィンドウには DWM サムネイルが描画されず、WPF も software 描画に落ちるため）。
    // 背景はコンテンツの隙間が 1 フレーム見えた場合に備えてダークにしておく。
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
            UseLayoutRounding = true;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        uint rop);
}
