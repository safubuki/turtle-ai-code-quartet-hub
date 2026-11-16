using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using TurtleAIQuartetHub.Panel;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// 完了時（Done）の少し早く滑らかなホタル点滅（AiDoneFirefly）および IDE / CLI 枠内非塗りつぶし（GroupHeaderCutout）の回帰を確認する。
internal static class AiDoneFireflyTests
{
    public const int Count = 9;

    public static void Run(List<string> failures)
    {
        VerifyFireflyTempoAndCurve(failures);
        VerifyGroupCutoutGeometry(failures);
        RunOnStaThread(() => VerifyRealXamlTemplatesAndStateTransitions(failures));
        VerifyGuideAndXamlWiring(failures);
    }

    private static void VerifyFireflyTempoAndCurve(List<string> failures)
    {
        var cycle = AiDoneFirefly.CycleDurationSeconds;
        var peakPhase = AiDoneFirefly.PeakSeconds / cycle;
        Check(
            failures,
            "完了ホタル周期: 5.6 秒より少し早い落ち着いた周期（2.6〜3.5 秒）であること",
            cycle is >= 2.6 and <= 3.5 && peakPhase is > 0.30 and < 0.45,
            $"cycle={cycle:F2}s, peakPhase={peakPhase:F3}");

        double i0 = 0, iStartSlope = 0, iMidRise = 0, iPrePeak = 0, iPeak = 0, iPostPeak = 0, iFadeMid = 0, iEndSlope = 0, iEnd = 0;
        RunOnStaThread(() =>
        {
            i0 = AiDoneFirefly.EvaluateIntensity(0.0);
            iStartSlope = AiDoneFirefly.EvaluateIntensity(0.03);
            iMidRise = AiDoneFirefly.EvaluateIntensity(0.20);
            iPrePeak = AiDoneFirefly.EvaluateIntensity(peakPhase - 0.04);
            iPeak = AiDoneFirefly.EvaluateIntensity(peakPhase);
            iPostPeak = AiDoneFirefly.EvaluateIntensity(peakPhase + 0.04);
            iFadeMid = AiDoneFirefly.EvaluateIntensity(0.68);
            iEndSlope = AiDoneFirefly.EvaluateIntensity(0.97);
            iEnd = AiDoneFirefly.EvaluateIntensity(0.9999);
        });

        // 始端・頂点・終端が水平接線で滑らかに繋がり、立ち上がりがやや早く余韻が長いこと。
        var smoothCurve = i0 < 0.005
            && iStartSlope < 0.03
            && iMidRise > 0.15 && iMidRise < 0.85
            && iPrePeak > 0.94
            && iPeak > 0.99
            && iPostPeak > 0.94
            && iFadeMid > 0.10 && iFadeMid < 0.70
            && iEndSlope < 0.03
            && iEnd < 0.005;

        Check(
            failures,
            "完了ホタル滑らか曲線: 周期端・頂点とも折れ角のない水平接線で、38%付近へ立ち上がり62%で長く滑らかに減衰すること",
            smoothCurve,
            $"start={iStartSlope:F3}, midR={iMidRise:F3}, preP={iPrePeak:F3}, peak={iPeak:F3}, postP={iPostPeak:F3}, midF={iFadeMid:F3}, end={iEndSlope:F3}");
    }

    private static void VerifyGroupCutoutGeometry(List<string> failures)
    {
        var geom = GroupHeaderCutout.BuildCutoutGeometry(120, 60, 8, 36);
        Check(
            failures,
            "IDE/CLI 枠線切り抜き: ラベル区間上辺(y=0.5)のみ切り抜かれ、枠外左右の上辺と枠内下側は保持されること",
            !geom.FillContains(new Point(20, 0.5))
            && geom.FillContains(new Point(4, 0.5))
            && geom.FillContains(new Point(60, 0.5))
            && geom.FillContains(new Point(20, 20)),
            "EvenOdd クリップ領域の判定が想定外");
    }

    private static void VerifyRealXamlTemplatesAndStateTransitions(List<string> failures)
    {
        var rootDir = FindRepoRoot();
        var resources = LoadMainWindowResources(Path.Combine(rootDir, "src", "TurtleAIQuartetHub.Panel", "MainWindow.xaml"));
        var peakPhase = AiDoneFirefly.PeakSeconds / AiDoneFirefly.CycleDurationSeconds;

        // 1. 標準表示カード展開と IDE / CLI 枠内非塗りつぶし + 完了滑らか点滅確認
        var stdSlot = CreateMockSlot("C", "AI スタディ", @"c:\git_home\agent-study", AiActivityState.Done);
        var stdHost = RenderTemplate(resources, "SlotCardTemplate", stdSlot, 295, 169);

        var headers = FindVisualChildren<TextBlock>(stdHost)
            .Where(tb => tb.Text is "IDE" or "CLI")
            .ToList();
        var allHeadersTransparent = headers.Count == 2
            && headers.All(tb => Equals((tb.Background as SolidColorBrush)?.Color, Colors.Transparent));

        var fillStyle = (Style)resources["ApplicationGroupFillStyle"];
        var fillBorders = FindVisualChildren<Border>(stdHost)
            .Where(b => ReferenceEquals(b.Style, fillStyle))
            .ToList();
        var fillDarkBg = fillBorders.Count == 2
            && fillBorders.All(b => ((SolidColorBrush)b.Background).Color == Color.FromRgb(0x12, 0x18, 0x16) && b.Margin.Top >= 1.0);

        var outlineStyle = (Style)resources["ApplicationGroupBorderStyle"];
        var outlineBorders = FindVisualChildren<Border>(stdHost)
            .Where(b => ReferenceEquals(b.Style, outlineStyle))
            .ToList();
        var outlineCutoutsReady = outlineBorders.Count == 2
            && outlineBorders.All(b => b.Clip is not null && !b.Clip.Bounds.IsEmpty);

        Check(
            failures,
            "IDE/CLI 見出し背景: 枠内背景(#121816)は塗られずラベルは完全透明で上辺枠線だけ切り抜かれること",
            allHeadersTransparent && fillDarkBg && outlineCutoutsReady,
            $"headers={headers.Count}(transparent={allHeadersTransparent}), fills={fillBorders.Count}(dark={fillDarkBg}), outlines={outlineBorders.Count}(clipped={outlineCutoutsReady})");

        AiDoneFirefly.FreezeAtPhaseForTesting(stdHost, 0.02);
        stdHost.UpdateLayout();
        var troughSnap = CapturePulseSnapshot(stdHost);

        AiDoneFirefly.FreezeAtPhaseForTesting(stdHost, peakPhase);
        stdHost.UpdateLayout();
        var peakSnap = CapturePulseSnapshot(stdHost);

        var pulseBrightensAllParts = troughSnap.HaloOpacity < 0.20 && peakSnap.HaloOpacity > 0.85
            && troughSnap.EdgeOpacity < 0.35 && peakSnap.EdgeOpacity > 0.90
            && troughSnap.CardWashOpacity < 0.05 && peakSnap.CardWashOpacity > 0.55
            && troughSnap.BadgeWashOpacity < 0.05 && peakSnap.BadgeWashOpacity > 0.55
            && troughSnap.BadgeGlowOpacity < 0.25 && peakSnap.BadgeGlowOpacity > 0.88;

        Check(
            failures,
            "標準カード完了ホタル点滅: 全ロールが Opacity ベースで溜め→ピークへ滑らかに連動発光すること",
            pulseBrightensAllParts,
            $"halo={troughSnap.HaloOpacity:F2}->{peakSnap.HaloOpacity:F2}, edge={troughSnap.EdgeOpacity:F2}->{peakSnap.EdgeOpacity:F2}, wash={troughSnap.CardWashOpacity:F2}->{peakSnap.CardWashOpacity:F2}, badge={troughSnap.BadgeWashOpacity:F2}->{peakSnap.BadgeWashOpacity:F2}, glow={troughSnap.BadgeGlowOpacity:F2}->{peakSnap.BadgeGlowOpacity:F2}");

        // 状態が Idle に戻ったら完了用オーバーレイがすべて Collapsed へ戻ること
        stdSlot.AiTracker.Acknowledge();
        stdSlot.NotifyAiActivityChanged();
        stdHost.UpdateLayout();
        var overlayAllCollapsed = FindVisualChildren<Border>(stdHost)
            .Where(b => AiDoneFirefly.GetRole(b) != AiDonePulseRole.None)
            .All(b => b.Visibility == Visibility.Collapsed);

        Check(
            failures,
            "完了→待機リセット: フォーカス切替等で Idle へ戻ると全完了オーバーレイが非表示・停止へ戻ること",
            overlayAllCollapsed);

        // 2. 縮小・極小テンプレートも同じく Opacity ベースで滑らかに点滅すること
        var compactSlot = CreateMockSlot("A", "てんかつ", @"c:\git_home\tenkatsu-tools", AiActivityState.Done);
        var compactHost = RenderTemplate(resources, "CompactSlotTemplate", compactSlot, 180, 28);
        AiDoneFirefly.FreezeAtPhaseForTesting(compactHost, 0.02);
        compactHost.UpdateLayout();
        var cTrough = CapturePulseSnapshot(compactHost);
        AiDoneFirefly.FreezeAtPhaseForTesting(compactHost, peakPhase);
        compactHost.UpdateLayout();
        var cPeak = CapturePulseSnapshot(compactHost);

        var microSlot = CreateMockSlot("B", "フュージョン", @"c:\git_home\fusion-creator", AiActivityState.Done);
        var microHost = RenderTemplate(resources, "MicroSlotTemplate", microSlot, 54, 29);
        AiDoneFirefly.FreezeAtPhaseForTesting(microHost, 0.02);
        microHost.UpdateLayout();
        var mTrough = CapturePulseSnapshot(microHost);
        AiDoneFirefly.FreezeAtPhaseForTesting(microHost, peakPhase);
        microHost.UpdateLayout();
        var mPeak = CapturePulseSnapshot(microHost);

        Check(
            failures,
            "縮小・極小カード完了ホタル点滅: 縮小カード地/バッジと極小タイルとも Opacity で溜め→ピークへ連動明滅すること",
            cTrough.CardWashOpacity < 0.05 && cPeak.CardWashOpacity > 0.55
            && cTrough.BadgeWashOpacity < 0.05 && cPeak.BadgeWashOpacity > 0.55
            && cTrough.BadgeGlowOpacity < 0.25 && cPeak.BadgeGlowOpacity > 0.88
            && mTrough.MicroWashOpacity < 0.05 && mPeak.MicroWashOpacity > 0.60
            && mTrough.HaloOpacity < 0.20 && mPeak.HaloOpacity > 0.85,
            $"compactWash={cTrough.CardWashOpacity:F2}->{cPeak.CardWashOpacity:F2}, microWash={mTrough.MicroWashOpacity:F2}->{mPeak.MicroWashOpacity:F2}");

        // 3. 完了(Done)のまま再実行(Working)へ移ったとき、3テンプレートとも完了表示が残り続けず LED 周回へ即切り替わり、
        //    中断(Interrupt)で即 Idle へ戻ること（フォーカス切替なし遷移の確認）
        var hosts = new[] { (stdSlot, stdHost), (compactSlot, compactHost), (microSlot, microHost) };
        var transitionOk = true;
        var now = DateTimeOffset.UtcNow;
        foreach (var (slot, host) in hosts)
        {
            slot.AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, slot.SavedWorkspacePath, "t"), now);
            slot.AiTracker.Apply(new AiHookEvent("claude", "Stop", AiHookEventKind.Stop, slot.SavedWorkspacePath, "t"), now);
            slot.NotifyAiActivityChanged();
            host.UpdateLayout();

            slot.AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, slot.SavedWorkspacePath, "t"), now.AddSeconds(2));
            slot.NotifyAiActivityChanged();
            host.UpdateLayout();

            var orbit = FindVisualChildren<AiCardOrbitGlow>(host).FirstOrDefault();
            var doneHidden = FindVisualChildren<Border>(host)
                .Where(b => AiDoneFirefly.GetRole(b) != AiDonePulseRole.None)
                .All(b => b.Visibility == Visibility.Collapsed);
            var workingVisible = orbit is not null && orbit.Visibility == Visibility.Visible;

            slot.AiTracker.Apply(new AiHookEvent("claude", "Interrupt", AiHookEventKind.Interrupt, slot.SavedWorkspacePath, "t"), now.AddSeconds(3));
            slot.NotifyAiActivityChanged();
            host.UpdateLayout();

            var idleCollapsed = orbit is not null && orbit.Visibility == Visibility.Collapsed;
            transitionOk &= doneHidden && workingVisible && idleCollapsed;
        }

        Check(
            failures,
            "完了→作業中→待機遷移: フォーカス切替なしで作業中へ移ると完了表示が消え周回へ切り替わり、中断で消灯すること",
            transitionOk);
    }

    private static void VerifyGuideAndXamlWiring(List<string> failures)
    {
        var rootDir = FindRepoRoot();
        var mainXaml = File.ReadAllText(Path.Combine(rootDir, "src", "TurtleAIQuartetHub.Panel", "MainWindow.xaml"));
        var guideXaml = File.ReadAllText(Path.Combine(rootDir, "src", "TurtleAIQuartetHub.Panel", "Views", "AiHookGuideView.xaml"));

        var hasDoneFireflyBindings =
            mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"Halo\"", StringComparison.Ordinal)
            && mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"CardEdge\"", StringComparison.Ordinal)
            && mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"CardSurface\"", StringComparison.Ordinal)
            && mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"MicroSurface\"", StringComparison.Ordinal)
            && mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"BadgeSurface\"", StringComparison.Ordinal)
            && mainXaml.Contains("Property=\"local:AiDoneFirefly.Role\" Value=\"BadgeGlow\"", StringComparison.Ordinal)
            && mainXaml.Contains("local:GroupHeaderCutout.Header=", StringComparison.Ordinal);

        var guideMentionsFirefly = guideXaml.Contains("少し早めのホタル点滅", StringComparison.Ordinal)
            && guideXaml.Contains("青のホタル点滅", StringComparison.Ordinal);

        Check(
            failures,
            "XAML・ガイド整合: 完了ホタル点滅（6ロール）と IDE/CLI 切り抜きが結線され、ガイド文言も一致すること",
            hasDoneFireflyBindings && guideMentionsFirefly,
            $"xamlWiring={hasDoneFireflyBindings}, guide={guideMentionsFirefly}");
    }

    private static (double HaloOpacity, double EdgeOpacity, double CardWashOpacity, double BadgeWashOpacity, double BadgeGlowOpacity, double MicroWashOpacity)
        CapturePulseSnapshot(DependencyObject root)
    {
        double halo = -1, edge = -1, cardWash = -1, badgeWash = -1, badgeGlow = -1, microWash = -1;

        foreach (var b in FindVisualChildren<Border>(root))
        {
            switch (AiDoneFirefly.GetRole(b))
            {
                case AiDonePulseRole.Halo:
                    halo = b.Opacity;
                    break;
                case AiDonePulseRole.CardEdge:
                    edge = b.Opacity;
                    break;
                case AiDonePulseRole.CardSurface:
                    cardWash = b.Opacity;
                    break;
                case AiDonePulseRole.BadgeSurface:
                    badgeWash = b.Opacity;
                    break;
                case AiDonePulseRole.BadgeGlow:
                    badgeGlow = b.Opacity;
                    break;
                case AiDonePulseRole.MicroSurface:
                    microWash = b.Opacity;
                    break;
            }
        }

        return (halo, edge, cardWash, badgeWash, badgeGlow, microWash);
    }

    private static Grid RenderTemplate(ResourceDictionary resources, string templateKey, WindowSlot slot, double width, double height)
    {
        var cc = new ContentControl
        {
            Content = slot,
            ContentTemplate = (DataTemplate)resources[templateKey],
            Width = width,
            Height = height,
            Resources = resources
        };

        var host = new Grid { Width = width, Height = height, Resources = resources };
        host.Children.Add(cc);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        cc.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        host.UpdateLayout();
        return host;
    }

    private static WindowSlot CreateMockSlot(string name, string title, string path, AiActivityState state)
    {
        var slot = new WindowSlot(new SlotConfig
        {
            Name = name,
            Path = path,
            ApplicationId = AppConfig.VsCodeApplicationId
        })
        {
            PanelTitle = title,
            SavedWorkspacePath = path,
            SavedWorkspaceConfirmed = true,
            WindowHandle = new IntPtr(12345),
            WindowStatus = SlotWindowStatus.Ready,
            IsVsCodeAvailable = true,
            IsAntigravityAvailable = true
        };

        foreach (var (id, shortName) in new[] { (AppConfig.VsCodeApplicationId, "VS Code"), ("antigravity", "Antigravity") })
        {
            var app = new LauncherApplication(new ToolApplicationConfig { Id = id, DisplayName = shortName, ShortName = shortName, Kind = ApplicationKind.WorkspaceIde });
            app.ApplyAvailability(ApplicationAvailabilityStatus.Installed, id, "mock");
            slot.IdeApplicationOptions.Add(new SlotApplicationOption(slot, app, id == AppConfig.VsCodeApplicationId));
        }

        foreach (var shortName in new[] { "Claude", "Codex", "Antigr...", "Grok", "Copilot" })
        {
            var id = shortName.ToLowerInvariant();
            var app = new LauncherApplication(new ToolApplicationConfig { Id = id, DisplayName = shortName, ShortName = shortName, Kind = ApplicationKind.WorkspaceCli });
            app.ApplyAvailability(ApplicationAvailabilityStatus.Installed, id, "mock");
            slot.CliApplicationOptions.Add(new SlotApplicationOption(slot, app, false));
        }

        var now = DateTimeOffset.UtcNow;
        if (state == AiActivityState.Done)
        {
            slot.AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, path, "t"), now);
            slot.AiTracker.Apply(new AiHookEvent("claude", "Stop", AiHookEventKind.Stop, path, "t"), now);
            slot.NotifyAiActivityChanged();
        }

        return slot;
    }

    private static ResourceDictionary LoadMainWindowResources(string xamlPath)
    {
        var doc = XDocument.Load(xamlPath);
        var root = doc.Root!;
        XNamespace wpfn = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resElem = root.Element(wpfn + "Window.Resources")!;

        var rd = new XElement(wpfn + "ResourceDictionary",
            root.Attributes().Where(a => a.IsNamespaceDeclaration),
            resElem.Elements());

        foreach (var es in rd.Descendants(wpfn + "EventSetter").ToList())
        {
            es.Remove();
        }

        var eventAttrNames = new HashSet<string>
        {
            "Click", "MouseLeftButtonUp", "PreviewMouseLeftButtonDown", "PreviewMouseMove",
            "DragEnter", "DragLeave", "DragOver", "Drop", "PreviewMouseRightButtonUp",
            "KeyDown", "PreviewKeyDown", "LostKeyboardFocus", "MouseDoubleClick", "PreviewMouseDown",
            "GotKeyboardFocus", "TextChanged", "Checked", "Unchecked", "SelectionChanged"
        };

        foreach (var el in rd.DescendantsAndSelf())
        {
            foreach (var attr in el.Attributes().Where(a => eventAttrNames.Contains(a.Name.LocalName)).ToList())
            {
                attr.Remove();
            }
        }

        var xamlText = Regex.Replace(
            rd.ToString(),
            "clr-namespace:TurtleAIQuartetHub\\.Panel([^;\"]*)\"",
            "clr-namespace:TurtleAIQuartetHub.Panel$1;assembly=TurtleAIQuartetHub\"");

        return (ResourceDictionary)XamlReader.Parse(xamlText);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
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
            failures.Add(detail is null ? $"AiDoneFirefly: {name}" : $"AiDoneFirefly: {name}: {detail}");
        }
    }
}
