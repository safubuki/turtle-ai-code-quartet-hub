using System.Windows;
using TurtleAIQuartetHub.Panel;
using TurtleAIQuartetHub.Panel.Services;

// メインパネルの最小化（タスクバーのボタンへ縮みながら吸い込まれる演出）の回帰チェック。
internal static class MinimizeAnimationTests
{
    public const int Count = 10;

    private const double Padding = 22.0;
    private const double FallbackSize = 44.0;
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);
    private static readonly Rect WorkArea = new(0, 0, 1920, 1032);
    private static readonly Rect Panel = new(635, 272, 650, 535);
    private static readonly Rect OwnButton = new(1353, 1032, 44, 48);

    public static void Run(List<string> failures)
    {
        TestShrinksIntoTaskbarButton(failures);
        TestEndScaleStaysWithinSaneRange(failures);
        TestFallbackTargetsSitOnTaskbarBand(failures);
        TestUnusableButtonFallsBackToTaskbarBand(failures);
        TestAutoHideTaskbarTargetsScreenEdge(failures);
        TestSuctionCurvesStartSoftlyAndStayVisibleUntilLanding(failures);
        TestSinkTimelineKeepsBriskButPerceptiblePace(failures);
        TestOwnTaskbarButtonMatching(failures);
        TestSoftenerFeathersOutline(failures);
        TestMinimizeGhostOffscreen(failures);
    }

    private static Point Center(Rect rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    private static void TestShrinksIntoTaskbarButton(List<string> failures)
    {
        var target = MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, WorkArea, OwnButton, FallbackSize);
        if (target != OwnButton)
        {
            failures.Add($"見つかったタスクバーのボタンが吸い込み先になっていません (target={target})。");
        }

        var plan = MainWindow.BuildMinimizeSinkPlan(Panel, target, Padding, Padding);

        // 最後のコマで、影武者の中心がボタンの中心に重なること。
        var landed = Center(Panel) + plan.Travel;
        if ((landed - Center(OwnButton)).Length > 1e-6)
        {
            failures.Add($"影武者がボタンの中心へ吸い込まれていません (landed={landed}, button={Center(OwnButton)})。");
        }

        // 最後の大きさがボタンとほぼ同じ（＝本当に縮む。前回の「四角いまま落ちる」の再発防止）。
        var endWidth = Panel.Width * plan.EndScale;
        var endHeight = Panel.Height * plan.EndScale;
        if (endWidth > OwnButton.Width + 1e-6 || endHeight > OwnButton.Height + 1e-6 || plan.EndScale > 0.1)
        {
            failures.Add($"最後の大きさがボタンより大きく、縮んで吸い込まれて見えません (end={endWidth:0}x{endHeight:0}, scale={plan.EndScale:0.000})。");
        }

        if (!plan.GhostArea.Contains(Panel) || !plan.GhostArea.Contains(OwnButton))
        {
            failures.Add($"影武者の範囲が最初のパネルとボタンの両方を含んでいません (area={plan.GhostArea})。");
        }
    }

    private static void TestEndScaleStaysWithinSaneRange(List<string> failures)
    {
        var tiny = MainWindow.BuildMinimizeSinkPlan(Panel, new Rect(900, 1050, 2, 2), Padding, Padding);
        var huge = MainWindow.BuildMinimizeSinkPlan(Panel, new Rect(0, 600, 1200, 1000), Padding, Padding);
        if (tiny.EndScale < 0.03 || huge.EndScale > 0.31)
        {
            failures.Add($"極端な着地点で最後の倍率が範囲外です (tiny={tiny.EndScale:0.000}, huge={huge.EndScale:0.000})。");
        }
    }

    private static void TestFallbackTargetsSitOnTaskbarBand(List<string> failures)
    {
        var bottom = MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, WorkArea, null, FallbackSize);
        var bottomCenter = Center(bottom);
        if (Math.Abs(bottomCenter.Y - 1056) > 1e-6 || Math.Abs(bottomCenter.X - Center(Panel).X) > 1e-6)
        {
            failures.Add($"ボタン不明時の着地点が下タスクバーの帯の上（パネルの真下）になっていません (center={bottomCenter})。");
        }

        var top = Center(MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, new Rect(0, 48, 1920, 1032), null, FallbackSize));
        var left = Center(MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, new Rect(62, 0, 1858, 1080), null, FallbackSize));
        var right = Center(MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, new Rect(0, 0, 1858, 1080), null, FallbackSize));
        if (Math.Abs(top.Y - 24) > 1e-6 || Math.Abs(left.X - 31) > 1e-6 || Math.Abs(right.X - 1889) > 1e-6)
        {
            failures.Add($"上・左・右タスクバーの帯の上へ着地点が置かれていません (top={top}, left={left}, right={right})。");
        }
    }

    private static void TestUnusableButtonFallsBackToTaskbarBand(List<string> failures)
    {
        // 別のディスプレイにあるボタン、自動的に隠す設定で画面外にあるボタン、大きさがおかしいボタンは使わない。
        Rect[] unusable =
        [
            new Rect(2500, 1032, 44, 48),
            new Rect(1353, 1078, 44, 48),
            new Rect(1353, 1032, 0, 0),
            new Rect(0, 0, 1920, 1080)
        ];
        foreach (var button in unusable)
        {
            var target = MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, WorkArea, button, FallbackSize);
            if (target == button || Math.Abs(Center(target).Y - 1056) > 1e-6)
            {
                failures.Add($"使えないボタン位置 {button} でタスクバーの帯の上へフォールバックしません (target={target})。");
            }
        }
    }

    private static void TestAutoHideTaskbarTargetsScreenEdge(List<string> failures)
    {
        var target = MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, Monitor, null, FallbackSize);
        if (!Monitor.Contains(target) || Math.Abs(target.Bottom - Monitor.Bottom) > 1e-6)
        {
            failures.Add($"タスクバー自動非表示時に画面下端の内側へ吸い込まれません (target={target})。");
        }

        var empty = MainWindow.ResolveMinimizeTargetRect(Panel, Monitor, Rect.Empty, null, FallbackSize);
        if (!Monitor.Contains(empty) || Center(empty).Y <= Center(Panel).Y)
        {
            failures.Add($"作業領域が取れないときに画面下側へフォールバックしません (target={empty})。");
        }
    }

    private static void TestSuctionCurvesStartSoftlyAndStayVisibleUntilLanding(List<string> failures)
    {
        var travel = MainWindow.MinimizeTravelSpline;
        var narrow = MainWindow.MinimizeNarrowSpline;
        var fade = MainWindow.MinimizeFadeSpline;

        // ふわっ: 動き出しはやさしく（その場でパッと消えない）、途中でスーッと加速し、最後はボタンへ収まる。
        var start = travel.GetSplineProgress(0.15);
        var middle = travel.GetSplineProgress(0.5);
        var late = travel.GetSplineProgress(0.9);
        if (!(start < 0.08 && middle > 0.3 && middle < 0.55 && late > 0.85))
        {
            failures.Add($"吸い込みの緩急が不正です (0.15={start:0.00}, 0.5={middle:0.00}, 0.9={late:0.00})。");
        }

        // 横幅は縦より少し先に細る（吸い口の形）。
        if (narrow.GetSplineProgress(0.5) <= travel.GetSplineProgress(0.5))
        {
            failures.Add("横幅の縮みが縦より先行しておらず、吸い込まれる形になりません。");
        }

        // 前半ははっきり見え（その場で消えない）、後半は空気に溶けるように薄れ、ボタンへ届く手前で消えきる。
        double OpacityAt(double t) => t >= MainWindow.MinimizeFadeEndRatio
            ? 0.0
            : 1.0 - fade.GetSplineProgress(t / MainWindow.MinimizeFadeEndRatio);
        var opacityEarly = OpacityAt(0.4);
        var opacityLate = OpacityAt(0.7);
        if (opacityEarly < 0.6 || opacityLate > 0.25 || OpacityAt(MainWindow.MinimizeFadeEndRatio) > 1e-6)
        {
            failures.Add($"薄れ方が不正です (0.4={opacityEarly:0.00}, 0.7={opacityLate:0.00})。");
        }

        // 消えきる時点では、もうボタンのすぐ手前まで来ている（届く頃には消えている）。
        var travelAtVanish = travel.GetSplineProgress(MainWindow.MinimizeFadeEndRatio);
        if (MainWindow.MinimizeFadeEndRatio > 0.9 || travelAtVanish < 0.8 || travelAtVanish > 0.95)
        {
            failures.Add($"消えきる時点がボタンへの到着と合っていません (ratio={MainWindow.MinimizeFadeEndRatio:0.00}, travel={travelAtVanish:0.00})。");
        }

        // くっきり → 軽いぼかし → 強いぼかしの順に、途切れずに受け渡す（輪郭がくっきり残らない）。
        if (!(MainWindow.MinimizeLightSoftInStartRatio < MainWindow.MinimizeCrispFadeStartRatio
              && MainWindow.MinimizeLightSoftInEndRatio < MainWindow.MinimizeCrispFadeEndRatio
              && MainWindow.MinimizeHeavySoftInStartRatio < MainWindow.MinimizeLightSoftOutStartRatio
              && MainWindow.MinimizeHeavySoftInEndRatio <= MainWindow.MinimizeLightSoftOutEndRatio
              && MainWindow.MinimizeCrispFadeEndRatio <= 0.5
              && MainWindow.MinimizeHeavySoftInEndRatio < MainWindow.MinimizeFadeEndRatio))
        {
            failures.Add("くっきり→軽いぼかし→強いぼかしの受け渡し順序が不正です。");
        }

        var previous = -1.0;
        for (var i = 0; i <= 20; i++)
        {
            var value = travel.GetSplineProgress(i / 20.0);
            if (value < previous - 1e-9)
            {
                failures.Add("吸い込みの移動曲線が単調増加になっていません。");
                break;
            }

            previous = value;
        }
    }

    private static void TestSinkTimelineKeepsBriskButPerceptiblePace(List<string> failures)
    {
        var (settle, lookupBudget, total) = MainWindow.GetMinimizeSinkTimelineForTesting();

        // アプリ全体のテンポをそろえるため、表示モード切替（標準・縮小・極小）と同じ長さにする。
        var modeSwitch = MainWindow.GetDisplayModeSwitchDurationForTesting();
        if (total != modeSwitch)
        {
            failures.Add($"最小化の長さが表示モード切替とそろっていません (minimize={total.TotalMilliseconds:0}ms, modeSwitch={modeSwitch.TotalMilliseconds:0}ms)。");
        }

        // 頻繁に使う操作なので待たされず、ただし吸い込まれる様子が見える長さ。
        if (total < TimeSpan.FromMilliseconds(200) || total > TimeSpan.FromMilliseconds(400))
        {
            failures.Add($"最小化の吸い込み時間が軽快さと知覚のバランスを外れています ({total.TotalMilliseconds:0}ms)。");
        }

        if (settle > TimeSpan.FromMilliseconds(30) || lookupBudget > TimeSpan.FromMilliseconds(80))
        {
            failures.Add($"押してから動き出すまでの待ちが長すぎます (settle={settle.TotalMilliseconds:0}ms, lookup={lookupBudget.TotalMilliseconds:0}ms)。");
        }
    }

    private static void TestOwnTaskbarButtonMatching(List<string> failures)
    {
        const string exe = @"C:\git_home\turtle-ai-code-quartet-hub\dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe";
        const string title = "Turtle AI Code Quartet Hub";

        if (!TaskbarButtonLocator.IsOwnTaskbarButton("Appid: " + exe.ToLowerInvariant(), "なんでも", exe, title))
        {
            failures.Add("Windows 11 の Appid（実行ファイルのパス）で自分のボタンを見つけられません。");
        }

        if (!TaskbarButtonLocator.IsOwnTaskbarButton(null, title + " - 1 個の実行中ウィンドウ 固定済み", exe, title)
            || !TaskbarButtonLocator.IsOwnTaskbarButton(string.Empty, title, exe, title))
        {
            failures.Add("ボタンの名前（タイトル）で自分のボタンを見つけられません。");
        }

        if (TaskbarButtonLocator.IsOwnTaskbarButton("Appid: C:\\other\\AIUsageChecker.exe", "Turtle AI Usage Checker - 1 個の実行中ウィンドウ", exe, title)
            || TaskbarButtonLocator.IsOwnTaskbarButton(null, title + " Helper", exe, title))
        {
            failures.Add("名前の似た別アプリのボタンを自分のボタンと誤認しました。");
        }
    }

    private static void TestSoftenerFeathersOutline(List<string> failures)
    {
        RunOnStaThread(failures, "輪郭ぼかし画像の生成検証", () =>
        {
            // 真っ白な不透明パネル 975x803（150% 表示の標準サイズ相当）。
            const int width = 975;
            const int height = 803;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)0xFF);
            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 144, 144, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, width * 4);
            source.Freeze();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var small = PanelImageSoftener.Downscale(source, 0.25);
            var light = PanelImageSoftener.Soften(small, 2);
            var heavy = PanelImageSoftener.Soften(small, 5);
            stopwatch.Stop();

            // 押してから動き出すまでを遅らせない（UI スレッドで作っても気にならない速さ）。
            if (stopwatch.ElapsedMilliseconds > 60)
            {
                throw new InvalidOperationException($"ぼかし画像の生成に時間がかかりすぎます ({stopwatch.ElapsedMilliseconds}ms)。");
            }

            if (heavy.Image.PixelWidth != small.PixelWidth + 30 || light.Image.PixelWidth != small.PixelWidth + 12)
            {
                throw new InvalidOperationException($"にじみ用の余白が不正です (light={light.Image.PixelWidth}, heavy={heavy.Image.PixelWidth}, small={small.PixelWidth})。");
            }

            if (Math.Abs(heavy.MarginFractionX - 15.0 / small.PixelWidth) > 1e-9 || heavy.MarginFractionY <= light.MarginFractionY)
            {
                throw new InvalidOperationException("にじみの割合が不正です。");
            }

            // 中央の行で、外側から内側へ向かって不透明度がなめらかに上がる（輪郭が外へにじんでいる）こと。
            var w = heavy.Image.PixelWidth;
            var h = heavy.Image.PixelHeight;
            var result = new byte[w * h * 4];
            heavy.Image.CopyPixels(result, w * 4, 0);
            var row = h / 2 * w * 4;
            var previous = -1;
            for (var x = 0; x <= 30; x++)
            {
                var alpha = result[row + x * 4 + 3];
                if (alpha < previous)
                {
                    throw new InvalidOperationException($"輪郭の外側ほど薄くなっていません (x={x}, alpha={alpha}, prev={previous})。");
                }

                previous = alpha;
            }

            var outer = result[row + 3];
            var edge = result[row + 15 * 4 + 3];
            var center = result[row + w / 2 * 4 + 3];
            if (!(outer < 10 && edge > 60 && edge < 200 && center == 255))
            {
                throw new InvalidOperationException($"輪郭のぼけ方が不正です (outer={outer}, edge={edge}, center={center})。");
            }

            // 乗算済みアルファの整合（色が不透明度を超えない＝透明部分へ色が浮かない）。
            for (var i = 0; i < result.Length; i += 4)
            {
                var a = result[i + 3];
                if (result[i] > a || result[i + 1] > a || result[i + 2] > a)
                {
                    throw new InvalidOperationException("ぼかし後の乗算済みアルファが崩れています。");
                }
            }
        });
    }

    private static void TestMinimizeGhostOffscreen(List<string> failures)
    {
        RunOnStaThread(failures, "最小化用の軽量透過影武者オフスクリーン検証", () =>
        {
            var (size, allowsTransparency, origin, heavySoftSize, padding) = MainWindow.VerifyMinimizeGhostOffscreenForTesting();
            if (!allowsTransparency)
            {
                throw new InvalidOperationException("最小化影武者で AllowsTransparency=True が有効になっていません。");
            }

            if (size.Width < 650 || size.Height < 535)
            {
                throw new InvalidOperationException($"最小化影武者の計測サイズが不正です ({size.Width:0}x{size.Height:0})。");
            }

            if (origin != new Point(0.5, 0.5))
            {
                throw new InvalidOperationException($"最小化影武者が中央を基準に縮んでいません (origin={origin})。");
            }

            // 強いぼかし層は輪郭の外へにじむぶん、パネルより大きく置かれていること。
            if (heavySoftSize.Width <= size.Width + 20 || heavySoftSize.Height <= size.Height + 20)
            {
                throw new InvalidOperationException($"ぼかし層が輪郭の外へにじんでいません (soft={heavySoftSize.Width:0}x{heavySoftSize.Height:0}, panel={size.Width:0}x{size.Height:0})。");
            }

            // 強いぼかしが見え始める時点の大きさで、にじみが影武者ウィンドウの余白に収まる（端で切れて四角く見えない）。
            var endScale = 44.0 / 650.0;
            var scaleAtHeavyStart = 1.0 - (1.0 - endScale) * MainWindow.MinimizeTravelSpline.GetSplineProgress(MainWindow.MinimizeHeavySoftInStartRatio);
            var bleedX = (heavySoftSize.Width * scaleAtHeavyStart - size.Width) / 2;
            var bleedY = (heavySoftSize.Height * scaleAtHeavyStart - size.Height) / 2;
            if (bleedX > padding || bleedY > padding)
            {
                throw new InvalidOperationException($"ぼかしのにじみが影武者の余白からはみ出します (bleed={bleedX:0.0}x{bleedY:0.0}, padding={padding:0})。");
            }
        });
    }

    private static void RunOnStaThread(List<string> failures, string label, Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
            failures.Add($"{label}: {captured.GetType().Name} - {captured.Message}");
        }
    }
}
