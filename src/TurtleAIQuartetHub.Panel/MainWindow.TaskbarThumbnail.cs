using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// タスクバーのアイコンへマウスオーバーしたときに出るサムネイル（プレビュー）を、極小表示の間だけ
// 新しい円形オービット・ダイヤルの画像へ差し替える。
//
// 極小表示中の MainWindow は DWMWA_CLOAK で隠しているだけなので、何もしないと DWM は隠れた本体側の
// 旧 MicroPanel をそのままライブサムネイルとして出してしまう。そこで円形ダイヤルが本番表示されている間
// （IsMicroDialActive）だけ DWM の iconic サムネイル（静止ビットマップ）方式へ切り替え、
// WM_DWMSENDICONICTHUMBNAIL の要求に「円形コアを非透過で描いた画像」を返す。
// 標準・縮小表示、本体を一時露出するモーダル表示中、Cloak 失敗時の MicroPanel フォールバック中は、
// 実際に見えている本体をそのまま映すべきなので、DWM 既定のライブサムネイルへ戻す。
// サムネイル以外（Cloak・最小化／復元演出・ダイヤル本体の描画や透過度）には一切触れない。
public partial class MainWindow
{
    private const int DwmwaForceIconicRepresentation = 7;
    private const int DwmwaHasIconicBitmap = 10;
    private const int WmDwmSendIconicThumbnail = 0x0323;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;

    private bool _isTaskbarIconicThumbnailEnabled;

    // 円形ダイヤルの稼働状態に合わせて、タスクバーのサムネイルを iconic（円形ダイヤル画像）／ライブ（既定）へ切り替える。
    // iconic 中は DWM が保持している画像を無効化し、次にサムネイルが必要になったとき最新のスロット状態で取り直させる
    // （無効化は印を付けるだけで、実際の再要求はサムネイル表示時にしか来ないため、周期更新から呼んでも軽い）。
    private void SyncTaskbarThumbnailRepresentation()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var shouldUseIconic = IsMicroDialActive;
        if (shouldUseIconic != _isTaskbarIconicThumbnailEnabled)
        {
            SetTaskbarIconicThumbnailEnabled(handle, shouldUseIconic);
        }

        if (_isTaskbarIconicThumbnailEnabled)
        {
            _ = DwmInvalidateIconicBitmaps(handle);
        }
    }

    private void SetTaskbarIconicThumbnailEnabled(IntPtr handle, bool enabled)
    {
        var value = enabled ? 1 : 0;
        var hasBitmapResult = DwmSetWindowAttribute(handle, DwmwaHasIconicBitmap, ref value, sizeof(int));
        var forceResult = DwmSetWindowAttribute(handle, DwmwaForceIconicRepresentation, ref value, sizeof(int));

        // 有効化に失敗した環境では、無理に iconic にせず従来どおりのライブサムネイルのままにする。
        if (enabled && (hasBitmapResult < 0 || forceResult < 0))
        {
            var off = 0;
            _ = DwmSetWindowAttribute(handle, DwmwaHasIconicBitmap, ref off, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaForceIconicRepresentation, ref off, sizeof(int));
            _isTaskbarIconicThumbnailEnabled = false;
            return;
        }

        _isTaskbarIconicThumbnailEnabled = enabled;
    }

    // WM_DWMSENDICONICTHUMBNAIL（lParam: HIWORD=最大幅, LOWORD=最大高さ）に、円形ダイヤルの非透過画像を返す。
    // 返せたときだけ true（既定処理を止める）。
    private bool TryProvideTaskbarIconicThumbnail(IntPtr hwnd, IntPtr lParam)
    {
        if (!_isTaskbarIconicThumbnailEnabled)
        {
            return false;
        }

        if (!IsMicroDialActive || _microDialWindow is not { } dial)
        {
            // 状態切替の取りこぼしがあっても、見えている本体のライブサムネイルへ戻す。
            SetTaskbarIconicThumbnailEnabled(hwnd, false);
            return false;
        }

        var packed = lParam.ToInt64();
        var maxWidth = (int)((packed >> 16) & 0xFFFF);
        var maxHeight = (int)(packed & 0xFFFF);
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var hBitmap = IntPtr.Zero;
        try
        {
            var image = dial.RenderTaskbarThumbnailImage(maxWidth, maxHeight);
            hBitmap = CreateTopDownDibSection(image);
            return hBitmap != IntPtr.Zero && DwmSetIconicThumbnail(hwnd, hBitmap, 0) >= 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(hBitmap);
            }
        }
    }

    // DwmSetIconicThumbnail が受け付ける 32bpp のトップダウン DIB を作り、Pbgra32 の画素（B,G,R,A 順）をそのまま写す。
    // サムネイル画像は全面不透明（α=255）なので、乗算済みアルファの差異も生じない。
    private static IntPtr CreateTopDownDibSection(BitmapSource image)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb
        };

        var hBitmap = CreateDIBSection(IntPtr.Zero, ref header, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(hBitmap);
            }

            return IntPtr.Zero;
        }

        var stride = width * 4;
        image.CopyPixels(new Int32Rect(0, 0, width, height), bits, stride * height, stride);
        return hBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbmp, uint dwSITFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfoHeader pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
