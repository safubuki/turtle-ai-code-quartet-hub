using System.IO;
using System.Windows;
using System.Windows.Media;
using TurtleAIQuartetHub.Panel;
using TurtleAIQuartetHub.Panel.Models;

// 作業中 LED 周回エフェクト（AiCardOrbitGlow）の枠線追従精度、境界クリップ防止、速度カーブの回帰を確認する。
internal static class AiOrbitGlowTests
{
    public const int Count = 8;

    public static void Run(List<string> failures)
    {
        VerifyPerimeterCornerAccuracy(failures);
        VerifyMotionProfileAndPeriod(failures);
        RunOnStaThread(() => VerifyVisualBoundsAndStateTransitions(failures));
        VerifyXamlWiring(failures);
    }

    private static void VerifyPerimeterCornerAccuracy(List<string> failures)
    {
        var cases = new (string Label, double Width, double Height, double CornerRadius)[]
        {
            ("標準", 295, 169, 8),
            ("縮小", 180, 24, 7),
            ("極小", 48, 26, 6)
        };

        foreach (var (label, width, height, cornerRadius) in cases)
        {
            var perimeter = AiCardOrbitGlow.CreatePerimeter(new Size(width, height), cornerRadius);
            var inset = AiCardOrbitGlow.BorderCenterInset;
            var r = Math.Min(cornerRadius - inset, Math.Min(width - 1, height - 1) * 0.5);
            var expectedLength = (2 * ((width - 1) - (2 * r))) + (2 * ((height - 1) - (2 * r))) + (2 * Math.PI * r);

            Check(
                failures,
                $"周回軌道({label}): 枠線中心線の周長が厳密式と一致",
                Math.Abs(perimeter.Length - expectedLength) < 1e-6,
                $"expected={expectedLength:F4}, actual={perimeter.Length:F4}");

            // 右上角円弧の中央地点が、円弧中心から半径 r の距離を正確に保っていること。
            var straightTop = (width - 1) - (2 * r);
            var quarterArc = Math.PI * 0.5 * r;
            var arcMid = perimeter.EvaluateAtDistance(straightTop + (quarterArc * 0.5));
            var center = new Point((width - inset) - r, inset + r);
            var actualRadius = Math.Sqrt(Math.Pow(arcMid.X - center.X, 2) + Math.Pow(arcMid.Y - center.Y, 2));

            Check(
                failures,
                $"周回軌道({label}): 角丸カーブ中点が枠線中心半径 r を正確に追従",
                Math.Abs(actualRadius - r) < 1e-6,
                $"expectedR={r:F4}, actualR={actualRadius:F4}, point={arcMid}");
        }
    }

    private static void VerifyMotionProfileAndPeriod(List<string> failures)
    {
        var microDuration = AiCardOrbitGlow.ComputeDurationSeconds(135);
        var compactDuration = AiCardOrbitGlow.ComputeDurationSeconds(385);
        var standardDuration = AiCardOrbitGlow.ComputeDurationSeconds(915);

        Check(
            failures,
            "周回周期: 目障りな高速回転にせず落ち着いた周期（標準 3.0〜4.2 秒、極小/縮小 2.4 秒以上）に収める",
            microDuration >= 2.4 && compactDuration >= 2.6 && standardDuration is >= 3.0 and <= 4.2,
            $"micro={microDuration:F2}s, compact={compactDuration:F2}s, standard={standardDuration:F2}s");

        var prev = -1.0;
        var monotonic = true;
        for (int i = 0; i < 100; i++)
        {
            var (progress, _) = AiCardOrbitGlow.EvaluateMotion(i / 100.0);
            if (progress < prev)
            {
                monotonic = false;
                break;
            }
            prev = progress;
        }

        var (pStart, _) = AiCardOrbitGlow.EvaluateMotion(0.0);
        var (pNearEnd, _) = AiCardOrbitGlow.EvaluateMotion(0.9999);
        var (pWrap, _) = AiCardOrbitGlow.EvaluateMotion(1.0);
        var (p10, _) = AiCardOrbitGlow.EvaluateMotion(0.10);
        var (p45, _) = AiCardOrbitGlow.EvaluateMotion(0.45);
        var (p72, _) = AiCardOrbitGlow.EvaluateMotion(0.72);
        var (p88, _) = AiCardOrbitGlow.EvaluateMotion(0.88);
        var (p02, _) = AiCardOrbitGlow.EvaluateMotion(0.02);
        var (p98, _) = AiCardOrbitGlow.EvaluateMotion(0.98);

        var earlySpeed = (p45 - p10) / 0.35;
        var lateSpeed = (p88 - p72) / 0.16;
        var ratio = lateSpeed / earlySpeed;
        var startEndDiff = Math.Abs((p02 / 0.02) - ((1.0 - p98) / 0.02));

        Check(
            failures,
            "速度カーブ: 単調増加で 0→1 が繋がり、前半ゆっくり→終盤に穏やか加速（1.4〜2.5 倍）で境界が滑らか",
            monotonic
            && Math.Abs(pStart) < 1e-6
            && pNearEnd > 0.999
            && Math.Abs(pWrap) < 1e-6
            && ratio is >= 1.4 and <= 2.5
            && startEndDiff < 0.15,
            $"early={earlySpeed:F3}, late={lateSpeed:F3}, ratio={ratio:F2}, seamDiff={startEndDiff:F3}");
    }

    private static void VerifyVisualBoundsAndStateTransitions(List<string> failures)
    {
        var specs = new (string Label, double Width, double Height, double CornerRadius, double MaxOutset)[]
        {
            ("標準", 295, 169, 8, 4.0),
            ("縮小", 180, 24, 7, 2.5),
            ("極小", 48, 26, 6, 2.5)
        };

        foreach (var (label, width, height, cornerRadius, maxOutset) in specs)
        {
            var glow = new AiCardOrbitGlow
            {
                CornerRadius = cornerRadius,
                Activity = AiActivityState.Working
            };

            glow.Measure(new Size(width, height));
            glow.Arrange(new Rect(0, 0, width, height));

            var maxObservedOutset = 0.0;
            foreach (var phase in new[] { 0.04, 0.25, 0.55, 0.80 })
            {
                glow.SetStaticPhase(phase);
                var bounds = glow.GetVisualContentBounds();
                if (bounds.IsEmpty)
                {
                    maxObservedOutset = double.PositiveInfinity;
                    break;
                }

                var leftOut = Math.Max(0, -bounds.Left);
                var topOut = Math.Max(0, -bounds.Top);
                var rightOut = Math.Max(0, bounds.Right - width);
                var bottomOut = Math.Max(0, bounds.Bottom - height);
                maxObservedOutset = Math.Max(maxObservedOutset, Math.Max(Math.Max(leftOut, topOut), Math.Max(rightOut, bottomOut)));
            }

            Check(
                failures,
                $"境界クリップ防止({label}): 枠外張り出しが {maxOutset:F1} DIP 以内に収まり四角いクリップ境界を出さない",
                !double.IsInfinity(maxObservedOutset) && maxObservedOutset > 0.2 && maxObservedOutset <= maxOutset && !glow.ClipToBounds,
                $"outset={maxObservedOutset:F2} DIP, limit={maxOutset:F2} DIP");
        }

        var stateGlow = new AiCardOrbitGlow { CornerRadius = 8, Activity = AiActivityState.Working };
        stateGlow.Measure(new Size(295, 169));
        stateGlow.Arrange(new Rect(0, 0, 295, 169));
        stateGlow.SetStaticPhase(0.25);
        var activeNonEmpty = stateGlow.Visibility == Visibility.Visible && !stateGlow.GetVisualContentBounds().IsEmpty;

        stateGlow.Activity = AiActivityState.Done;
        var clearedOnDone = stateGlow.Visibility == Visibility.Collapsed && stateGlow.GetVisualContentBounds().IsEmpty;

        Check(
            failures,
            "状態遷移: Working 中のみ描画され、Done/Idle で即座に描画と時計がクリアされる",
            activeNonEmpty && clearedOnDone,
            $"activeNonEmpty={activeNonEmpty}, clearedOnDone={clearedOnDone}");
    }

    private static void VerifyXamlWiring(List<string> failures)
    {
        var root = FindRepoRoot();
        var mainXaml = File.ReadAllText(Path.Combine(root, "src", "TurtleAIQuartetHub.Panel", "MainWindow.xaml"));
        var guideXaml = File.ReadAllText(Path.Combine(root, "src", "TurtleAIQuartetHub.Panel", "Views", "AiHookGuideView.xaml"));

        var orbitOccurrences = CountOccurrences(mainXaml, "<local:AiCardOrbitGlow");
        var guideHasLedPath = guideXaml.Contains("M 10,0.5 L 29,0.5 A 4.5,4.5 0 0 1 33.5,5 L 33.5,11", StringComparison.Ordinal)
            && !guideXaml.Contains("Width=\"16\" Height=\"8\"", StringComparison.Ordinal);

        Check(
            failures,
            "XAML 統合: 標準・縮小・極小の 3 カードすべてに AiCardOrbitGlow が配置され、ガイドも LED 図解に統一",
            orbitOccurrences == 3 && guideHasLedPath,
            $"orbitOccurrences={orbitOccurrences}, guideHasLedPath={guideHasLedPath}");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TurtleAIQuartetHub.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("TurtleAIQuartetHub.sln が見つかりませんでした。");
    }

    private static void RunOnStaThread(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

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
        thread.Join();

        if (captured is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(detail is null ? $"AiOrbitGlow: {name}" : $"AiOrbitGlow: {name}: {detail}");
        }
    }
}
