using System.Windows;
using System.Windows.Media.Animation;
using TurtleAIQuartetHub.Panel;

// メインパネルの最小化からの復元（タスクバーのボタンから広がって元の位置に収まる演出）の回帰チェック。
internal static class RestoreAnimationTests
{
    public const int Count = 5;

    public static void Run(List<string> failures)
    {
        TestRestoreWindowPosDetection(failures);
        TestReversedCurvesMirrorMinimize(failures);
        TestMirroredBlendEndsAsTheRealPanel(failures);
        TestRestoreTimelineStaysResponsive(failures);
        TestRestoreGhostOffscreen(failures);
    }

    private static void TestRestoreWindowPosDetection(List<string> failures)
    {
        // 実機の WPF ウィンドウで記録した WM_WINDOWPOSCHANGING の値。
        // 復元: (20, 20) へ SWP_STATECHANGED | SWP_NOCOPYBITS | SWP_FRAMECHANGED。
        if (!MainWindow.IsRestoreWindowPos(20, 20, 0x8120))
        {
            failures.Add("最小化からの復元（画面内の位置へ戻す WINDOWPOS）を見逃しました。");
        }

        // 最小化（画面外 -32000 への退避）、重なり順だけの変更、移動しない変更は復元ではない。
        (int X, int Y, uint Flags)[] notRestore =
        [
            (-32000, -32000, 0x8170),
            (0, 0, 0x0003),
            (0, 0, 0x0037),
            (0, 0, 0x1001837),
            (100, 100, 0x0002)
        ];
        foreach (var (x, y, flags) in notRestore)
        {
            if (MainWindow.IsRestoreWindowPos(x, y, flags))
            {
                failures.Add($"復元ではない WINDOWPOS (x={x}, y={y}, flags=0x{flags:X}) を復元と誤認しました。");
            }
        }
    }

    private static void TestReversedCurvesMirrorMinimize(List<string> failures)
    {
        (string Name, KeySpline Minimize, KeySpline Restore)[] pairs =
        [
            ("移動", MainWindow.MinimizeTravelSpline, MainWindow.RestoreTravelSpline),
            ("横の広がり", MainWindow.MinimizeNarrowSpline, MainWindow.RestoreNarrowSpline),
            ("現れ方", MainWindow.MinimizeFadeSpline, MainWindow.RestoreFadeSpline)
        ];
        foreach (var (name, minimize, restore) in pairs)
        {
            for (var i = 0; i <= 20; i++)
            {
                var t = i / 20.0;
                var expected = 1.0 - minimize.GetSplineProgress(1.0 - t);
                var actual = restore.GetSplineProgress(t);
                if (Math.Abs(expected - actual) > 2e-3)
                {
                    failures.Add($"復元の{name}の曲線が最小化のちょうど逆になっていません (t={t:0.00}, expected={expected:0.000}, actual={actual:0.000})。");
                    break;
                }
            }
        }

        // 逆再生なので、ボタンから素早く離れ、元の位置へはふわっと減速して収まる。
        var travel = MainWindow.RestoreTravelSpline;
        if (!(travel.GetSplineProgress(0.2) > 0.15 && travel.GetSplineProgress(0.9) > 0.95))
        {
            failures.Add("復元の動きが、元の位置へふわっと減速して収まる形になっていません。");
        }
    }

    private static void TestMirroredBlendEndsAsTheRealPanel(List<string> failures)
    {
        var crisp = MainWindow.MirrorBlendPoints(MainWindow.MinimizeCrispBlend);
        var light = MainWindow.MirrorBlendPoints(MainWindow.MinimizeLightSoftBlend);
        var heavy = MainWindow.MirrorBlendPoints(MainWindow.MinimizeHeavySoftBlend);

        foreach (var (name, points) in new[] { ("くっきり", crisp), ("軽いぼかし", light), ("強いぼかし", heavy) })
        {
            if (Math.Abs(points[0].Ratio) > 1e-9 || Math.Abs(points[^1].Ratio - 1.0) > 1e-9)
            {
                failures.Add($"復元の{name}の時刻表が 0〜1 を覆っていません。");
            }

            for (var i = 1; i < points.Length; i++)
            {
                if (points[i].Ratio < points[i - 1].Ratio)
                {
                    failures.Add($"復元の{name}の時刻表が時刻順になっていません。");
                    break;
                }
            }
        }

        // はじめはボタン付近の強いぼかし（もや）だけ。最後は本物と同じく、くっきりした層だけが不透明。
        if (crisp[0].Value != 0.0 || light[0].Value != 0.0 || heavy[0].Value != 1.0)
        {
            failures.Add($"復元のはじめが強いぼかしのもやになっていません (crisp={crisp[0].Value}, light={light[0].Value}, heavy={heavy[0].Value})。");
        }

        if (crisp[^1].Value != 1.0 || light[^1].Value != 0.0 || heavy[^1].Value != 0.0)
        {
            failures.Add($"復元の最後が本物と同じ見た目になっていません (crisp={crisp[^1].Value}, light={light[^1].Value}, heavy={heavy[^1].Value})。");
        }
    }

    private static void TestRestoreTimelineStaysResponsive(List<string> failures)
    {
        var (settle, total) = MainWindow.GetRestoreRevealTimelineForTesting();
        var (_, _, minimizeTotal) = MainWindow.GetMinimizeSinkTimelineForTesting();

        // 最小化・表示モード切替と同じ長さにそろえる（復元中は本物が隠れていて操作できないので、それより長くしない）。
        if (total != minimizeTotal
            || total != MainWindow.GetDisplayModeSwitchDurationForTesting()
            || settle > TimeSpan.FromMilliseconds(30))
        {
            failures.Add($"復元の演出時間が不正です (total={total.TotalMilliseconds:0}ms, minimize={minimizeTotal.TotalMilliseconds:0}ms, settle={settle.TotalMilliseconds:0}ms)。");
        }

        // はじめからボタン付近で現れ始める（現れ方の曲線は 0 から立ち上がり、前半のうちに半分ほど見える）。
        var fade = MainWindow.RestoreFadeSpline;
        if (fade.GetSplineProgress(0.1) <= 0.0 || fade.GetSplineProgress(0.5) < 0.3)
        {
            failures.Add("復元で影武者がボタン付近からすぐに現れ始めません。");
        }
    }

    private static void TestRestoreGhostOffscreen(List<string> failures)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (allowsTransparency, origin) = MainWindow.VerifyRestoreGhostOffscreenForTesting();
                if (!allowsTransparency)
                {
                    throw new InvalidOperationException("復元影武者で AllowsTransparency=True が有効になっていません。");
                }

                if (origin != new Point(0.5, 0.5))
                {
                    throw new InvalidOperationException($"復元影武者が中央を基準に広がっていません (origin={origin})。");
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        if (captured is not null)
        {
            failures.Add($"復元用の軽量透過影武者オフスクリーン検証: {captured.GetType().Name} - {captured.Message}");
        }
    }
}
