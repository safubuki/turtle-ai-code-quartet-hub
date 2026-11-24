using TurtleAIQuartetHub.Panel;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class LaunchPresentationTests
{
    public const int Count = 22;

    public static async Task RunAsync(List<string> failures)
    {
        TestKnownWindowIsNeverCloaked(failures);
        TestIneligibleOrUnrelatedWindowIsIgnored(failures);
        TestSpeculativeCloakAndSignal(failures);
        TestAdoptAndReleaseUnadoptedSeparately(failures);
        TestBatchAdoptAndIdempotentReleaseAll(failures);
        TestSequentialSingleSlotReleaseLeavesOthersCloaked(failures);
        TestDirectAdoptCloaksAndProtectsFromRecapture(failures);
        await TestFailsafeReleasesStuckCloaksAsync(failures);
        TestDisposeReleasesRemainingCloaks(failures);
        TestSlotCellBoundsLayoutConsistency(failures);
        TestNonActiveOverlayRevealStillInvokesReleaseCallback(failures);
        TestBatchOverlayVisualTreeOffscreen(failures);
        TestSingleOverlayVisualTreeOffscreen(failures);
        TestCloseOverlayVisualTreeOffscreen(failures);
        TestSharedProfileReattachesMatchingUnassignedWindow(failures);
        TestSharedProfileIgnoresOtherSlotsAssignedWindow(failures);
        TestStartupSmoothFadeCurveAndBorderColorInterpolation(failures);
        TestRevealWaveTimingsProvideCalmSequentialLampPacing(failures);
        TestBeginTimeNormalizationNeverBlocksAnimationStart(failures);
        TestCloseDoneHoldProvidesPerceptibleConfirmationPause(failures);
        TestFullScreenBackdropPolicy(failures);
        TestOverlayFailsafeAutoDismissesStuckBackdrop(failures);
    }

    private static void TestKnownWindowIsNeverCloaked(List<string> failures)
    {
        var cloakCalls = new List<(IntPtr Handle, bool Cloaked)>();
        var existing = new IntPtr(0x1001);
        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakCalls.Add((h, c));
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "Code");

        session.RegisterKnownHandles([existing]);
        session.AddTargetProcessNames(["code"]);

        var result = session.TrySpeculativeCloak(existing);
        if (result || cloakCalls.Count != 0 || session.SpeculativeCount != 0)
        {
            failures.Add("既知ウィンドウが先制クロークされてしまいました。");
        }
    }

    private static void TestIneligibleOrUnrelatedWindowIsIgnored(List<string> failures)
    {
        var cloakCalls = new List<(IntPtr Handle, bool Cloaked)>();
        var dialogHandle = new IntPtr(0x2001);
        var browserHandle = new IntPtr(0x2002);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakCalls.Add((h, c));
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: h => h != dialogHandle,
            resolveProcessName: h => h == browserHandle ? "msedge" : "Code");

        session.AddTargetProcessNames(["Code.exe"]);

        if (session.TrySpeculativeCloak(dialogHandle))
        {
            failures.Add("不適格ウィンドウ（ダイアログ・ツールウィンドウ等）が先制クロークされました。");
        }

        if (session.TrySpeculativeCloak(browserHandle))
        {
            failures.Add("対象外プロセスのウィンドウが先制クロークされました。");
        }

        if (cloakCalls.Count != 0)
        {
            failures.Add($"無視すべきウィンドウに対してクローク API が {cloakCalls.Count} 回呼ばれました。");
        }
    }

    private static void TestSpeculativeCloakAndSignal(List<string> failures)
    {
        var cloakCalls = new List<(IntPtr Handle, bool Cloaked)>();
        var transitionCalls = new List<(IntPtr Handle, bool Disabled)>();
        var handle = new IntPtr(0x3001);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakCalls.Add((h, c));
                return true;
            },
            setTransitionsDisabled: (h, d) =>
            {
                transitionCalls.Add((h, d));
                return true;
            },
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "Antigravity IDE");

        session.AddTargetProcessNames(["antigravity ide"]);

        var first = session.TrySpeculativeCloak(handle);
        var second = session.TrySpeculativeCloak(handle);

        if (!first || !second)
        {
            failures.Add("新規対象ウィンドウの先制クロークが成功しませんでした。");
        }

        if (cloakCalls.Count != 1 || transitionCalls.Count != 1 || session.SpeculativeCount != 1)
        {
            failures.Add("同一ウィンドウへの重複イベントでクローク API が二重適用されました。");
        }
    }

    private static void TestAdoptAndReleaseUnadoptedSeparately(List<string> failures)
    {
        var cloakedState = new Dictionary<IntPtr, bool>();
        var transitionState = new Dictionary<IntPtr, bool>();
        var mainWindow = new IntPtr(0x4001);
        var strayWindow = new IntPtr(0x4002);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakedState[h] = c;
                return true;
            },
            setTransitionsDisabled: (h, d) =>
            {
                transitionState[h] = d;
                return true;
            },
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "code");

        session.AddTargetProcessNames(["code"]);
        _ = session.TrySpeculativeCloak(mainWindow);
        _ = session.TrySpeculativeCloak(strayWindow);

        var adopted = session.Adopt(mainWindow);
        session.ReleaseUnadopted();

        if (!adopted || session.AdoptedCount != 1 || session.SpeculativeCount != 0)
        {
            failures.Add("採用と未採用の分離管理が期待通りになっていません。");
        }

        if (!cloakedState.GetValueOrDefault(mainWindow) || !transitionState.GetValueOrDefault(mainWindow))
        {
            failures.Add("採用済みウィンドウのクロークが ReleaseUnadopted で誤って解除されました。");
        }

        if (cloakedState.GetValueOrDefault(strayWindow) || transitionState.GetValueOrDefault(strayWindow))
        {
            failures.Add("不採用の先制クロークウィンドウが ReleaseUnadopted で解放されませんでした。");
        }
    }

    private static void TestBatchAdoptAndIdempotentReleaseAll(List<string> failures)
    {
        var uncloakCount = 0;
        var cloakedState = new Dictionary<IntPtr, bool>();
        var handles = new[] { new IntPtr(0x5001), new IntPtr(0x5002), new IntPtr(0x5003), new IntPtr(0x5004) };

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakedState[h] = c;
                if (!c)
                {
                    uncloakCount++;
                }

                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "WindowsTerminal");

        session.AddTargetProcessNames(["windowsterminal"]);
        foreach (var h in handles)
        {
            _ = session.TrySpeculativeCloak(h);
            _ = session.Adopt(h);
        }

        var firstReleased = session.ReleaseAllAdopted();
        var secondReleased = session.ReleaseAllAdopted();

        if (firstReleased.Count != 4 || secondReleased.Count != 0)
        {
            failures.Add($"一括クローク解除の件数または冪等性が不正です (1回目={firstReleased.Count}, 2回目={secondReleased.Count})。");
        }

        if (uncloakCount != 4 || handles.Any(h => cloakedState.GetValueOrDefault(h)))
        {
            failures.Add("4面一括解除後にクローク解除漏れまたは二重解除が発生しました。");
        }
    }

    private static void TestSequentialSingleSlotReleaseLeavesOthersCloaked(List<string> failures)
    {
        var cloakedState = new Dictionary<IntPtr, bool>();
        var a = new IntPtr(0x5501);
        var b = new IntPtr(0x5502);
        var c = new IntPtr(0x5503);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, cloaked) =>
            {
                cloakedState[h] = cloaked;
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "code");

        session.AddTargetProcessNames(["code"]);
        _ = session.Adopt(a);
        _ = session.Adopt(b);
        _ = session.Adopt(c);

        var releasedA = session.ReleaseAdopted(a);
        var releasedAAgain = session.ReleaseAdopted(a);
        if (!releasedA || releasedAAgain || cloakedState[a] || !cloakedState[b] || !cloakedState[c] || session.AdoptedCount != 2)
        {
            failures.Add("ウェーブ点灯用の個別クローク解除（ReleaseAdopted）が期待通り他スロットを維持できませんでした。");
        }

        var remaining = session.ReleaseAllAdopted();
        if (remaining.Count != 2 || cloakedState[b] || cloakedState[c])
        {
            failures.Add("個別解除後の残余一括解放（ReleaseAllAdopted）が正しく完了しませんでした。");
        }
    }

    private static void TestDirectAdoptCloaksAndProtectsFromRecapture(List<string> failures)
    {
        var cloakCalls = new List<(IntPtr Handle, bool Cloaked)>();
        var handle = new IntPtr(0x6001);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakCalls.Add((h, c));
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "code");

        session.AddTargetProcessNames(["code"]);
        var adopted = session.Adopt(handle);
        var recaptured = session.TrySpeculativeCloak(handle);

        if (!adopted || recaptured || cloakCalls.Count != 1 || !session.GetAdoptedHandles().Contains(handle))
        {
            failures.Add("直接 Adopt したウィンドウのクロークまたは再捕捉防止が正しく動作しませんでした。");
        }
    }

    private static async Task TestFailsafeReleasesStuckCloaksAsync(List<string> failures)
    {
        var cloakedState = new Dictionary<IntPtr, bool>();
        var handle = new IntPtr(0x7001);

        using var session = new LaunchWindowCloakSession(
            TimeSpan.FromMilliseconds(90),
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakedState[h] = c;
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "code");

        session.AddTargetProcessNames(["code"]);
        _ = session.Adopt(handle);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (cloakedState.GetValueOrDefault(handle) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (cloakedState.GetValueOrDefault(handle) || session.AdoptedCount != 0)
        {
            failures.Add("フェイルセーフタイマー経過後もクロークが解放されませんでした。");
        }
    }

    private static void TestDisposeReleasesRemainingCloaks(List<string> failures)
    {
        var cloakedState = new Dictionary<IntPtr, bool>();
        var adoptedHandle = new IntPtr(0x8001);
        var speculativeHandle = new IntPtr(0x8002);

        var session = new LaunchWindowCloakSession(
            TimeSpan.Zero,
            enableWinEventHook: false,
            setCloaked: (h, c) =>
            {
                cloakedState[h] = c;
                return true;
            },
            setTransitionsDisabled: (_, _) => true,
            isEligibleWindow: _ => true,
            resolveProcessName: _ => "code");

        session.AddTargetProcessNames(["code"]);
        _ = session.Adopt(adoptedHandle);
        _ = session.TrySpeculativeCloak(speculativeHandle);

        session.Dispose();

        if (cloakedState.GetValueOrDefault(adoptedHandle) || cloakedState.GetValueOrDefault(speculativeHandle))
        {
            failures.Add("Dispose 時に未解放のクロークが解除されませんでした。");
        }

        if (session.Adopt(new IntPtr(0x8003)) || session.TrySpeculativeCloak(new IntPtr(0x8004)))
        {
            failures.Add("Dispose 後のセッションが新規クロークを受け付けてしまいました。");
        }
    }

    private static void TestSlotCellBoundsLayoutConsistency(List<string> failures)
    {
        var arranger = new WindowArranger();
        var slots = new[]
        {
            new WindowSlot(new SlotConfig { Name = "A" }),
            new WindowSlot(new SlotConfig { Name = "B" }),
            new WindowSlot(new SlotConfig { Name = "C" }),
            new WindowSlot(new SlotConfig { Name = "D" })
        };

        var cells = new WindowArranger.WindowBounds[4];
        for (var i = 0; i < 4; i++)
        {
            if (!arranger.TryGetSlotCellBounds(slots[i], i, gap: 6, baseMonitorIndex: 0, out cells[i]))
            {
                failures.Add($"スロット {slots[i].Name} のセル座標を取得できませんでした。");
                return;
            }
        }

        if (cells[0].Width != cells[1].Width || cells[0].Height != cells[2].Height)
        {
            failures.Add("2x2 セルの幅・高さが 4 象限で揃っていません。");
        }

        if (cells[1].Left - (cells[0].Left + cells[0].Width) != 6
            || cells[2].Top - (cells[0].Top + cells[0].Height) != 6)
        {
            failures.Add("2x2 セルの間隔が指定 gap (6px) と一致していません。");
        }

        if (arranger.TryGetSlotCellBounds(slots[0], -1, 6, 0, out _)
            || arranger.TryGetSlotCellBounds(slots[0], 4, 6, 0, out _))
        {
            failures.Add("範囲外スロットインデックスに対して TryGetSlotCellBounds が true を返しました。");
        }
    }

    private static void TestNonActiveOverlayRevealStillInvokesReleaseCallback(List<string> failures)
    {
        var releaseCount = 0;
        using var overlay = new LaunchStagingOverlay(new WindowArranger());
        overlay.RevealAndCloseAsync(() => releaseCount++, "READY", "表示します").GetAwaiter().GetResult();

        if (releaseCount != 1)
        {
            failures.Add($"オーバーレイ非表示時の RevealAndCloseAsync でクローク解除コールバックが {releaseCount} 回呼ばれました（期待値 1）。");
        }
    }

    private static void TestBatchOverlayVisualTreeOffscreen(List<string> failures)
    {
        RunOnStaThread(failures, "4面一括起動用 HUD/象限ベールのオフスクリーン検証", () =>
        {
            var entries = new List<LaunchStageSlotEntry>
            {
                new("A", 0, "Project Alpha", "VS Code", LaunchStageSlotState.Pending, IsTarget: true),
                new("B", 1, "Project Beta", "Antigravity", LaunchStageSlotState.Pending, IsTarget: true),
                new("C", 2, "Project Gamma", "Claude", LaunchStageSlotState.Pending, IsTarget: true),
                new("D", 3, "Project Delta", "VS Code", LaunchStageSlotState.AlreadyRunning, IsTarget: false)
            };

            var (hudSize, veilSize, veilAllowsTransparency, backdropSize, backdropAllowsTransparency) = LaunchStagingOverlay.VerifyVisualTreeOffscreenForTesting(
                entries,
                "QUARTET LAUNCHING",
                "4面のワークスペースを裏で起動・整列しています...");

            if (!veilAllowsTransparency || !backdropAllowsTransparency)
            {
                throw new InvalidOperationException("象限ベールまたは黒い全画面シールドで AllowsTransparency=True が有効になっていません。");
            }

            if (backdropSize.Width <= 0 || backdropSize.Height <= 0)
            {
                throw new InvalidOperationException($"黒い全画面シールドの計測サイズが不正です ({backdropSize.Width:0}x{backdropSize.Height:0})。");
            }

            if (hudSize.Width < 400 || hudSize.Height < 140)
            {
                throw new InvalidOperationException($"一括起動 HUD の計測サイズが不正です ({hudSize.Width:0}x{hudSize.Height:0})。");
            }

            if (veilSize.Width <= 0 || veilSize.Height <= 0)
            {
                throw new InvalidOperationException($"象限ベールの計測サイズが不正です ({veilSize.Width:0}x{veilSize.Height:0})。");
            }
        });
    }

    private static void TestSingleOverlayVisualTreeOffscreen(List<string> failures)
    {
        RunOnStaThread(failures, "単体起動用 HUD/象限ベールのオフスクリーン検証", () =>
        {
            var entries = new List<LaunchStageSlotEntry>
            {
                new("A", 0, "Solo Workspace", "VS Code", LaunchStageSlotState.Pending, IsTarget: true),
                new("B", 1, "Other", "VS Code", LaunchStageSlotState.AlreadyRunning, IsTarget: false)
            };

            var (hudSize, veilSize, veilAllowsTransparency, backdropSize, backdropAllowsTransparency) = LaunchStagingOverlay.VerifyVisualTreeOffscreenForTesting(
                entries,
                "LAUNCHING SLOT A",
                "スロットAのVS Codeを準備しています...");

            if (!veilAllowsTransparency || !backdropAllowsTransparency || hudSize.Width < 340 || hudSize.Height < 140 || veilSize.Width <= 0 || veilSize.Height <= 0 || backdropSize.Width <= 0 || backdropSize.Height <= 0)
            {
                throw new InvalidOperationException($"単体起動 HUD/ベールの計測サイズが不正です (HUD={hudSize.Width:0}x{hudSize.Height:0}, Veil={veilSize.Width:0}x{veilSize.Height:0})。");
            }
        });
    }

    private static void TestCloseOverlayVisualTreeOffscreen(List<string> failures)
    {
        RunOnStaThread(failures, "一括終了用 HUD/象限ベールのオフスクリーン検証", () =>
        {
            var entries = new List<LaunchStageSlotEntry>
            {
                new("A", 0, "Project Alpha", "VS Code", LaunchStageSlotState.Closing, IsTarget: true),
                new("B", 1, "Project Beta", "VS Code", LaunchStageSlotState.Closing, IsTarget: true),
                new("C", 2, "Project Gamma", "Claude", LaunchStageSlotState.Closing, IsTarget: true),
                new("D", 3, "Project Delta", "Antigravity", LaunchStageSlotState.Closing, IsTarget: true)
            };

            var (hudSize, veilSize, veilAllowsTransparency, backdropSize, backdropAllowsTransparency) = LaunchStagingOverlay.VerifyVisualTreeOffscreenForTesting(
                entries,
                "QUARTET CLOSING",
                "4個のワークスペースを裏で終了しています...",
                isClosingMode: true);

            if (!veilAllowsTransparency || !backdropAllowsTransparency || hudSize.Width < 400 || hudSize.Height < 140 || veilSize.Width <= 0 || veilSize.Height <= 0 || backdropSize.Width <= 0 || backdropSize.Height <= 0)
            {
                throw new InvalidOperationException($"終了モード HUD/ベールの計測サイズが不正です (HUD={hudSize.Width:0}x{hudSize.Height:0}, Veil={veilSize.Width:0}x{veilSize.Height:0})。");
            }
        });
    }

    private static void TestSharedProfileReattachesMatchingUnassignedWindow(List<string> failures)
    {
        var slotAWindow = new WindowInfo(new IntPtr(0x9001), "README.md - ai-usage-checker - Visual Studio Code", 7096);
        var orphanDWindow = new WindowInfo(new IntPtr(0x9002), "SKILL.md - gemini-gem-to-skills - Visual Studio Code", 7096);
        var excluded = new HashSet<IntPtr> { slotAWindow.Handle };

        var matched = VscodeLauncher.SelectMatchingSharedProfileWindow(
            [slotAWindow, orphanDWindow],
            @"c:\git_home\gemini-gem-to-skills",
            excluded);

        if (matched is null || matched.Handle != orphanDWindow.Handle)
        {
            failures.Add("標準共有プロファイルで一致する未割り当て VS Code ウィンドウを再接続できませんでした。");
        }
    }

    private static void TestSharedProfileIgnoresOtherSlotsAssignedWindow(List<string> failures)
    {
        var alreadyAssignedWindow = new WindowInfo(new IntPtr(0x9101), "SKILL.md - gemini-gem-to-skills - Visual Studio Code", 7096);
        var excluded = new HashSet<IntPtr> { alreadyAssignedWindow.Handle };

        var matched = VscodeLauncher.SelectMatchingSharedProfileWindow(
            [alreadyAssignedWindow],
            @"c:\git_home\gemini-gem-to-skills",
            excluded);
        var emptyPathMatched = VscodeLauncher.SelectMatchingSharedProfileWindow(
            [alreadyAssignedWindow],
            launchPath: null,
            excludedHandles: null);

        if (matched is not null)
        {
            failures.Add("他スロットに既に割り当て済みの VS Code ウィンドウを誤って再接続対象にしました。");
        }

        if (emptyPathMatched is not null)
        {
            failures.Add("空ワークスペース起動時に無関係な既存 VS Code ウィンドウを誤って再接続対象にしました。");
        }
    }

    private static void TestStartupSmoothFadeCurveAndBorderColorInterpolation(List<string> failures)
    {
        var atZero = MainWindow.EvaluateSmoothFadeProgress(0.0);
        var atHalf = MainWindow.EvaluateSmoothFadeProgress(0.5);
        var atOne = MainWindow.EvaluateSmoothFadeProgress(1.0);
        if (Math.Abs(atZero) > 1e-6 || Math.Abs(atHalf - 0.5) > 1e-6 || Math.Abs(atOne - 1.0) > 1e-6)
        {
            failures.Add($"起動フェード曲線の境界値が不正です (0={atZero:0.000}, 0.5={atHalf:0.000}, 1={atOne:0.000})。");
        }

        var prev = -1.0;
        for (var i = 0; i <= 20; i++)
        {
            var current = MainWindow.EvaluateSmoothFadeProgress(i / 20.0);
            if (current < prev)
            {
                failures.Add("起動フェード曲線が単調増加になっていません。");
                break;
            }

            prev = current;
        }

        RunOnStaThread(failures, "本体起動・終了用の軽量透過影武者オフスクリーン検証", () =>
        {
            var (size, allowsTransparency) = MainWindow.VerifyStartupGhostOffscreenForTesting();
            if (!allowsTransparency)
            {
                throw new InvalidOperationException("本体起動・終了影武者で AllowsTransparency=True が有効になっていません。");
            }

            if (size.Width < 650 || size.Height < 535)
            {
                throw new InvalidOperationException($"起動影武者の計測サイズが不正です ({size.Width:0}x{size.Height:0})。");
            }
        });
    }

    private static void TestRevealWaveTimingsProvideCalmSequentialLampPacing(List<string> failures)
    {
        var timings = LaunchStagingOverlay.BuildRevealWaveTimings(4);
        if (timings.Count != 4)
        {
            failures.Add($"4スロット分のウェーブ時刻表件数が不正です (count={timings.Count})。");
            return;
        }

        for (var i = 0; i < timings.Count; i++)
        {
            var item = timings[i];
            if (!(item.GlowStartMs < item.UncloakAtMs && item.UncloakAtMs < item.FadeStartMs && item.FadeDurationMs >= 500))
            {
                failures.Add($"スロット {i} のランプ点灯→クローク解除→開放の順序または長さが不正です。");
            }

            if (i > 0)
            {
                var stepGapMs = item.GlowStartMs - timings[i - 1].GlowStartMs;
                if (stepGapMs < 450)
                {
                    failures.Add($"スロット間の点灯間隔が短すぎて世話しないテンポになっています ({stepGapMs:0}ms)。");
                }
            }
        }
    }

    private static void TestBeginTimeNormalizationNeverBlocksAnimationStart(List<string> failures)
    {
        if (LaunchStagingOverlay.NormalizeBeginTime(TimeSpan.Zero) != TimeSpan.Zero
            || LaunchStagingOverlay.NormalizeBeginTime(TimeSpan.FromMilliseconds(-50)) != TimeSpan.Zero
            || LaunchStagingOverlay.NormalizeBeginTime(TimeSpan.FromMilliseconds(240)) != TimeSpan.FromMilliseconds(240))
        {
            failures.Add("NormalizeBeginTime の正規化結果が期待値と一致しません。");
            return;
        }

        // 非表示の 1x1 HwndSource 上で、BeginTime=TimeSpan.Zero のフェードインが実際に完了すること、
        // および続く BeginTime>0 のフェードアウト待機中に Opacity が 1.0 のまま保持されることを検証する。
        RunOnStaThread(failures, "BeginTime=TimeSpan.Zero の実フェード完了と待機中不透明保持検証", () =>
        {
            var border = new System.Windows.Controls.Border { Opacity = 0.0 };
            var sourceParams = new System.Windows.Interop.HwndSourceParameters("LaunchPresentationAnimTest")
            {
                Width = 1,
                Height = 1,
                PositionX = -30000,
                PositionY = -30000,
                WindowStyle = 0
            };

            using var hwndSource = new System.Windows.Interop.HwndSource(sourceParams)
            {
                RootVisual = border
            };

            var completed = false;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new System.Windows.Duration(TimeSpan.FromMilliseconds(50)))
            {
                BeginTime = LaunchStagingOverlay.NormalizeBeginTime(TimeSpan.Zero)
            };
            fadeIn.Completed += (_, _) =>
            {
                border.Opacity = 1.0;
                completed = true;
                frame.Continue = false;
            };

            var timeout = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                frame.Continue = false;
            };
            timeout.Start();

            border.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timeout.Stop();

            if (!completed || border.Opacity < 0.99)
            {
                throw new InvalidOperationException($"BeginTime=Zero のフェードインが完了しませんでした (completed={completed}, opacity={border.Opacity:0.00})。");
            }

            // ローカル値 1.0 を確定した状態で BeginTime=200ms のフェードアウトを掛けても、
            // 開始前の瞬間に 0.0 へ落ちないこと。
            border.Opacity = 1.0;
            var delayedFadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, new System.Windows.Duration(TimeSpan.FromMilliseconds(50)))
            {
                BeginTime = LaunchStagingOverlay.NormalizeBeginTime(TimeSpan.FromMilliseconds(200))
            };
            border.BeginAnimation(System.Windows.UIElement.OpacityProperty, delayedFadeOut);
            if (border.Opacity < 0.99)
            {
                throw new InvalidOperationException($"BeginTime 待機中の Opacity がローカル値 1.0 を保持できませんでした (opacity={border.Opacity:0.00})。");
            }
        });
    }

    private static void TestCloseDoneHoldProvidesPerceptibleConfirmationPause(List<string> failures)
    {
        var batchHold = LaunchStagingOverlay.GetCloseDoneHoldDuration(isBatch: true);
        var singleHold = LaunchStagingOverlay.GetCloseDoneHoldDuration(isBatch: false);
        if (batchHold < TimeSpan.FromMilliseconds(600))
        {
            failures.Add($"一括終了の完了知覚ワンクッションが短すぎます ({batchHold.TotalMilliseconds:0}ms)。");
        }

        if (singleHold < TimeSpan.FromMilliseconds(400) || singleHold > batchHold)
        {
            failures.Add($"単体終了の完了知覚ワンクッションが不適切です (single={singleHold.TotalMilliseconds:0}ms, batch={batchHold.TotalMilliseconds:0}ms)。");
        }
    }

    private static void TestFullScreenBackdropPolicy(List<string> failures)
    {
        if (!LaunchStagingOverlay.ShouldUseFullScreenBackdrop(isClosingMode: false, veilTargetCount: 4)
            || !LaunchStagingOverlay.ShouldUseFullScreenBackdrop(isClosingMode: false, veilTargetCount: 1))
        {
            failures.Add("起動時に黒い全画面シールドが有効化されませんでした。");
        }

        if (!LaunchStagingOverlay.ShouldUseFullScreenBackdrop(isClosingMode: true, veilTargetCount: 4))
        {
            failures.Add("複数一括終了時に黒い全画面シールドが有効化されませんでした。");
        }

        if (LaunchStagingOverlay.ShouldUseFullScreenBackdrop(isClosingMode: true, veilTargetCount: 1)
            || LaunchStagingOverlay.ShouldUseFullScreenBackdrop(isClosingMode: false, veilTargetCount: 0))
        {
            failures.Add("単体終了または対象 0 件時に不要な黒い全画面シールドが有効化されました。");
        }
    }

    private static void TestOverlayFailsafeAutoDismissesStuckBackdrop(List<string> failures)
    {
        RunOnStaThread(failures, "黒画面・マスク残留防止フェイルセーフ自動解除検証", () =>
        {
            using var overlay = new LaunchStagingOverlay(new WindowArranger());
            overlay.ArmFailsafeForTesting(TimeSpan.FromMilliseconds(70));
            if (!overlay.IsActive)
            {
                throw new InvalidOperationException("テスト用フェイルセーフ待機状態に入れませんでした。");
            }

            var frame = new System.Windows.Threading.DispatcherFrame();
            var poll = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            var started = DateTimeOffset.UtcNow;
            poll.Tick += (_, _) =>
            {
                if (!overlay.IsActive || DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(2))
                {
                    poll.Stop();
                    frame.Continue = false;
                }
            };
            poll.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            if (overlay.IsActive)
            {
                throw new InvalidOperationException("フェイルセーフ期限超過後も LaunchStagingOverlay が自動解除されませんでした。");
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
