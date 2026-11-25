using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurtleAIQuartetHub.Panel.Services;

// 影武者を「輪郭ごとふわっとぼかして」消すための、あらかじめぼかしたパネル画像。
// Image は周囲に透明な余白を足してからぼかしてあるので、輪郭が外側へにじんで空気に溶けるように見える。
// MarginFractionX / Y は、その余白が元の画像の幅・高さに対してどれだけかの割合（表示するときに外側へ広げる量）。
internal readonly record struct SoftenedPanelImage(BitmapSource Image, double MarginFractionX, double MarginFractionY);

// 透過影武者に BlurEffect を掛けて毎フレームぼかすとコマ飛びするため（implementation-patterns §19）、
// 撮影したパネル画像を縮小してから CPU で一度だけぼかしておき、演出中は重ねた画像を入れ替えるだけにする。
// 縮小した画像（数万画素）に箱型ぼかしを 3 回かけるので、数 ms で終わる（3 回の箱型ぼかしはガウスぼかしにほぼ等しい）。
internal static class PanelImageSoftener
{
    private const int BoxBlurPasses = 3;

    // 元の画像を downscale 倍に縮小した画像（Pbgra32）。ぼかす前に一度だけ作り、強さの違うぼかしで使い回す。
    public static BitmapSource Downscale(BitmapSource source, double downscale)
    {
        var factor = Math.Clamp(downscale, 0.05, 1.0);
        BitmapSource scaled = factor < 1.0
            ? new TransformedBitmap(source, new ScaleTransform(factor, factor))
            : source;
        if (scaled.Format != PixelFormats.Pbgra32)
        {
            scaled = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        }

        // 中身を確定させて、別スレッドからも使える画像にする。
        var width = Math.Max(1, scaled.PixelWidth);
        var height = Math.Max(1, scaled.PixelHeight);
        var pixels = new byte[width * height * 4];
        scaled.CopyPixels(pixels, width * 4, 0);
        var copy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        copy.Freeze();
        return copy;
    }

    // radius（縮小後の画素数）の箱型ぼかしを 3 回かけた画像。にじみが届く 3×radius の透明な余白を周囲に足す。
    public static SoftenedPanelImage Soften(BitmapSource small, int radius)
    {
        var r = Math.Max(1, radius);
        var width = small.PixelWidth;
        var height = small.PixelHeight;
        var margin = r * BoxBlurPasses;
        var paddedWidth = width + margin * 2;
        var paddedHeight = height + margin * 2;

        var source = new byte[width * height * 4];
        small.CopyPixels(source, width * 4, 0);

        var buffer = new byte[paddedWidth * paddedHeight * 4];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(source, y * width * 4, buffer, ((y + margin) * paddedWidth + margin) * 4, width * 4);
        }

        var scratch = new byte[buffer.Length];
        for (var pass = 0; pass < BoxBlurPasses; pass++)
        {
            BoxBlurHorizontal(buffer, scratch, paddedWidth, paddedHeight, r);
            BoxBlurVertical(scratch, buffer, paddedWidth, paddedHeight, r);
        }

        var image = BitmapSource.Create(paddedWidth, paddedHeight, 96, 96, PixelFormats.Pbgra32, null, buffer, paddedWidth * 4);
        image.Freeze();
        return new SoftenedPanelImage(image, (double)margin / width, (double)margin / height);
    }

    // 乗算済みアルファ（Pbgra32）のまま 4 つの値を同じ重みでぼかす。外側は透明（0）として扱うので、
    // 輪郭は外へ向かって自然に薄れていく。乗算済みなので色が透明部分へ黒くにじむこともない。
    private static void BoxBlurHorizontal(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var x = 0; x <= radius && x < width; x++)
                {
                    sum += source[row + x * 4 + channel];
                }

                for (var x = 0; x < width; x++)
                {
                    destination[row + x * 4 + channel] = (byte)((sum + window / 2) / window);
                    var entering = x + radius + 1;
                    if (entering < width)
                    {
                        sum += source[row + entering * 4 + channel];
                    }

                    var leaving = x - radius;
                    if (leaving >= 0)
                    {
                        sum -= source[row + leaving * 4 + channel];
                    }
                }
            }
        }
    }

    private static void BoxBlurVertical(byte[] source, byte[] destination, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        var stride = width * 4;
        for (var x = 0; x < width; x++)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                var column = x * 4 + channel;
                var sum = 0;
                for (var y = 0; y <= radius && y < height; y++)
                {
                    sum += source[y * stride + column];
                }

                for (var y = 0; y < height; y++)
                {
                    destination[y * stride + column] = (byte)((sum + window / 2) / window);
                    var entering = y + radius + 1;
                    if (entering < height)
                    {
                        sum += source[entering * stride + column];
                    }

                    var leaving = y - radius;
                    if (leaving >= 0)
                    {
                        sum -= source[leaving * stride + column];
                    }
                }
            }
        }
    }
}
