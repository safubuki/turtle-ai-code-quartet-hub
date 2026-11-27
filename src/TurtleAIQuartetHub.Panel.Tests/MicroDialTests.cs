using System.Text.Json;
using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class MicroDialTests
{
    public const int Count = 33;

    public static void Run(List<string> failures)
    {
        TestSmartLabelsSkipSharedTokenPrefix(failures);
        TestSmartLabelsSkipSharedJapanesePrefix(failures);
        TestSmartLabelsDefaultTitleReturnsEmpty(failures);
        TestGhostOpacityConfigDefaultAndBackwardCompat(failures);
        TestMicroDialOffscreenStructureOpacityAndWheel(failures);
        TestMicroDialGhostOpacitySubmenuTenPercentSteps(failures);
        TestMicroDialContextMenuOutsideClickDismissal(failures);
        TestStandaloneExitConfirmWindowStructureAndPlacement(failures);
        TestCompactAndMicroExitConfirmAvoidMainWindowResizeAndUncloak(failures);
        TestMicroDialCenterMenuHasLaunchAllAndCloseAll(failures);
        TestMicroDialCaptureCoreCircleImage(failures);
        TestMicroDialCaptureCoreCircleImageCenteredWithoutVerticalClip(failures);
        TestMicroDialCoreShadowConcentricWithCore(failures);
        TestMicroDialNonActivatingMenuClickAndJumpGuards(failures);
        TestMicroDialCenterButtonSupportsDragAndSingleClickToggle(failures);
        TestMicroDialInnerWorkingArcBrighterAndThicker(failures);
        TestMicroDialOuterWorkingOrbitRunsOnlyWhileAnySlotWorking(failures);
        TestMicroDialIdleOpacityThinWhenFocusedOrWorkingAndDenseOnlyWhenDone(failures);
        TestMicroDialRadialLabelSymmetry(failures);
        TestCompactAndMicroStoredOverviewScrollableWithoutScrollBar(failures);
        TestMicroDialStoredAssignAndHiddenStateTransitions(failures);
        TestMicroDialModeButtonIconUsesCircularDialVector(failures);
        TestStandardAndCompactModeIconsUseAbsoluteVectorShapes(failures);
        TestMicroDialTopArcUsesAbsoluteStandardAndCompactVectorIcons(failures);
        TestMicroDialBottomArcOrderAndStoredPillClearance(failures);
        TestMicroDialDarkContextMenuAndStatusVisualContrast(failures);
        TestMicroDialMissingSlotClickDoesNotLaunch(failures);
        TestMicroDialAuxMenuButtonTogglesOpenAndClose(failures);
        TestStandardTitleBarHasMicroJumpButtonNextToCompactButton(failures);
        TestMicroDialLeftArcHasDownloadsAndPullRequestButtons(failures);
        TestFocusNameOverlayPlacedBelowMicroDial(failures);
        TestMicroDialTaskbarThumbnailIsOpaqueWithoutGhostOpacity(failures);
        TestMainWindowTaskbarThumbnailShowsDialOnlyWhileMicroDialActive(failures);
    }

    private static void TestSmartLabelsSkipSharedTokenPrefix(List<string> failures)
    {
        var slots = new List<WindowSlot>
        {
            MakeSlot("A", "AI-Usage-Checker"),
            MakeSlot("B", "タートルHub"),
            MakeSlot("C", "AI-Agent-Lab"),
            MakeSlot("D", "Gemini-Skills")
        };

        var labels = WindowSlot.BuildMicroSmartLabels(slots);
        if (!string.Equals(labels["A"], "Usage", StringComparison.Ordinal)
            || !string.Equals(labels["C"], "Agent", StringComparison.Ordinal))
        {
            failures.Add($"共通接頭辞 AI- のスキップが期待どおりではありません (A='{labels["A"]}', C='{labels["C"]}')。");
        }

        if (!string.Equals(labels["B"], "タートル", StringComparison.Ordinal)
            || !string.Equals(labels["D"], "Gemini", StringComparison.Ordinal))
        {
            failures.Add($"非重複タイトルの短縮名が期待どおりではありません (B='{labels["B"]}', D='{labels["D"]}')。");
        }
    }

    private static void TestSmartLabelsSkipSharedJapanesePrefix(List<string> failures)
    {
        var slots = new List<WindowSlot>
        {
            MakeSlot("A", "タートル本体"),
            MakeSlot("B", "タートル管理"),
            MakeSlot("C", "別プロジェクト"),
            MakeSlot("D", "スロット D")
        };

        var labels = WindowSlot.BuildMicroSmartLabels(slots);
        if (!string.Equals(labels["A"], "本体", StringComparison.Ordinal)
            || !string.Equals(labels["B"], "管理", StringComparison.Ordinal))
        {
            failures.Add($"日本語の共通先頭文字列のスキップに失敗しました (A='{labels["A"]}', B='{labels["B"]}')。");
        }
    }

    private static void TestSmartLabelsDefaultTitleReturnsEmpty(List<string> failures)
    {
        var slots = new List<WindowSlot>
        {
            MakeSlot("A", "スロット A"),
            MakeSlot("B", string.Empty),
            MakeSlot("C", "   "),
            MakeSlot("D", "スロットD 2")
        };

        var labels = WindowSlot.BuildMicroSmartLabels(slots);
        if (slots.Any(slot => !string.IsNullOrEmpty(labels[slot.Name])))
        {
            failures.Add("未命名・既定タイトルのスロットで空文字以外の短縮ラベルが返されました。");
        }
    }

    private static void TestGhostOpacityConfigDefaultAndBackwardCompat(List<string> failures)
    {
        var fresh = new AppConfig();
        if (!fresh.MicroGhostOpacityEnabled || fresh.MicroGhostOpacityPercent != AppConfig.DefaultMicroGhostOpacityPercent)
        {
            failures.Add($"AppConfig の極小透過既定値が期待（true / {AppConfig.DefaultMicroGhostOpacityPercent}%）と異なります ({fresh.MicroGhostOpacityEnabled} / {fresh.MicroGhostOpacityPercent}%)。");
        }

        // 旧設定ファイル（MicroGhostOpacityEnabled / MicroGhostOpacityPercent キー無し）を読み込んでも既定値を維持すること。
        var legacy = JsonSerializer.Deserialize<AppConfig>("""{"CodeCommand":"code","Gap":6}""");
        if (legacy is null || !legacy.MicroGhostOpacityEnabled || legacy.MicroGhostOpacityPercent != 30)
        {
            failures.Add("旧設定 JSON からの復元時に極小透過既定値（true / 30%）が維持されませんでした。");
        }

        // 10〜90% の 10% 刻み正規化（範囲外クランプ＆端数丸め）
        if (AppConfig.NormalizeMicroGhostOpacityPercent(0) != 10
            || AppConfig.NormalizeMicroGhostOpacityPercent(26) != 30
            || AppConfig.NormalizeMicroGhostOpacityPercent(34) != 30
            || AppConfig.NormalizeMicroGhostOpacityPercent(95) != 90)
        {
            failures.Add("AppConfig.NormalizeMicroGhostOpacityPercent のクランプ・10% 刻み丸めが期待と異なります。");
        }
    }

    private static void TestMicroDialOffscreenStructureOpacityAndWheel(List<string> failures)
    {
        RunOnStaThread(failures, "円形オービット・ダイヤルのオフスクリーン構造・透過・ホイール検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var result = dial.VerifyOffscreenStateForTesting();
                if (!result.AllowsTransparency)
                {
                    throw new InvalidOperationException("MicroDialWindow の AllowsTransparency が true ではありません。");
                }

                if (result.StageSize.Width < MicroDialWindow.StageSizeDip - 1
                    || result.StageSize.Height < MicroDialWindow.StageSizeDip - 1)
                {
                    throw new InvalidOperationException($"MicroDialWindow のステージ実寸が不足しています ({result.StageSize.Width:0}x{result.StageSize.Height:0})。");
                }

                if (Math.Abs(result.IdleGhostOpacityValue - 0.30) > 0.01)
                {
                    throw new InvalidOperationException($"非ホバー既定透過率が期待（0.30）と異なります ({result.IdleGhostOpacityValue:0.00})。");
                }

                if (!(result.IdleAiAlertOpacityValue > result.IdleGhostOpacityValue + 0.4))
                {
                    throw new InvalidOperationException($"AI 通知時の透過ブーストが効いていません (idle={result.IdleGhostOpacityValue:0.00}, ai={result.IdleAiAlertOpacityValue:0.00})。");
                }

                if (result.ExpandedOpacityValue < 0.99 || result.SatelliteOpacityWhenExpanded < 0.99)
                {
                    throw new InvalidOperationException($"ホバー展開時に実体化・サテライト表示されません (dial={result.ExpandedOpacityValue:0.00}, sat={result.SatelliteOpacityWhenExpanded:0.00})。");
                }

                if (result.WheelNextIndex != 1)
                {
                    throw new InvalidOperationException($"ホイール回転で次のスロットへ進みませんでした (index={result.WheelNextIndex})。");
                }

                if (!string.Equals(result.FirstSectorShortLabel, "Usage", StringComparison.Ordinal)
                    || !string.Equals(result.ThirdSectorShortLabel, "Agent", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"扇の短縮ラベルにスマート識別名が反映されていません (A='{result.FirstSectorShortLabel}', C='{result.ThirdSectorShortLabel}')。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialGhostOpacitySubmenuTenPercentSteps(List<string> failures)
    {
        RunOnStaThread(failures, "右クリックメニューの 10% 刻み透過度サブメニューと反映検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var callbackHistory = new List<(bool Enabled, int Percent)>();
            var dial = new MicroDialWindow(
                slots,
                ghostOpacityEnabled: true,
                getAuxApplications: () => [],
                onSlotActivated: _ => { },
                onStoredPanelDropped: (_, _) => { },
                onSlotSwapped: (_, _) => { },
                onToggleVisibility: () => { },
                onToggleStoredOverview: () => { },
                onPinTop: () => { },
                onSendBack: () => { },
                onLaunchAuxApp: _ => { },
                onSwitchStandard: () => { },
                onSwitchCompact: () => { },
                onMinimize: () => { },
                onRequestClose: () => { },
                onGhostOpacityChanged: (enabled, percent) => callbackHistory.Add((enabled, percent)),
                onRecenterRequested: () => { },
                onCoreCenterMoved: _ => { });
            try
            {
                var info = dial.VerifyGhostOpacitySubmenuForTesting();
                if (!string.Equals(info.InitialGestureText, "30%", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"既定の透過表示ラベルが期待（30%）と異なります ('{info.InitialGestureText}')。");
                }

                // 10%〜90% の 9 項目＋「透過しない（100%）」の計 10 項目が揃っていること
                if (info.SubmenuHeaders.Count != 10)
                {
                    throw new InvalidOperationException($"透過度サブメニューの項目数が 10 ではありません ({info.SubmenuHeaders.Count})。");
                }

                for (var p = 10; p <= 90; p += 10)
                {
                    if (!info.SubmenuHeaders.Any(h => h.StartsWith($"{p}%", StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException($"透過度サブメニューに {p}% の選択肢がありません。");
                    }
                }

                if (!info.SubmenuHeaders.Any(h => h.Contains("透過しない", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("透過度サブメニューに「透過しない（100%）」がありません。");
                }

                if (!string.Equals(info.GestureAfter20Percent, "20%", StringComparison.Ordinal)
                    || Math.Abs(info.IdleOpacityAt20Percent - 0.20) > 0.01
                    || info.AiAlertOpacityAt20Percent < 0.85)
                {
                    throw new InvalidOperationException(
                        $"20% 選択時の反映が期待と異なります (label='{info.GestureAfter20Percent}', idle={info.IdleOpacityAt20Percent:0.00}, ai={info.AiAlertOpacityAt20Percent:0.00})。");
                }

                if (Math.Abs(info.IdleOpacityAt90Percent - 0.90) > 0.01
                    || Math.Abs(info.AiAlertOpacityAt90Percent - 0.90) > 0.01)
                {
                    throw new InvalidOperationException(
                        $"90% 選択時に AI 通知で不透明度が下がっています (idle={info.IdleOpacityAt90Percent:0.00}, ai={info.AiAlertOpacityAt90Percent:0.00})。");
                }

                if (!string.Equals(info.GestureAfterDisabled, "OFF", StringComparison.Ordinal)
                    || Math.Abs(info.IdleOpacityWhenDisabled - 1.0) > 0.01)
                {
                    throw new InvalidOperationException(
                        $"透過しない選択時の反映が期待と異なります (label='{info.GestureAfterDisabled}', idle={info.IdleOpacityWhenDisabled:0.00})。");
                }

                if (callbackHistory.Count != 3
                    || callbackHistory[0] != (true, 20)
                    || callbackHistory[1] != (true, 90)
                    || callbackHistory[2] != (false, 90))
                {
                    throw new InvalidOperationException("透過度変更コールバックの通知履歴が期待と異なります。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialContextMenuOutsideClickDismissal(List<string> failures)
    {
        RunOnStaThread(failures, "右クリックメニューのメニュー外クリック・Esc閉鎖・サブメニュー保護検証", () =>
        {
            if (!MicroDialWindow.IsOutsideDismissMouseMessage(0x0201)
                || !MicroDialWindow.IsOutsideDismissMouseMessage(0x0204)
                || !MicroDialWindow.IsOutsideDismissMouseMessage(0x0207)
                || MicroDialWindow.IsOutsideDismissMouseMessage(0x0200))
            {
                throw new InvalidOperationException("マウス押下判定（WM_MOUSEMOVE 除外）が期待と異なります。");
            }

            if (!MicroDialWindow.IsEscapeDismissKeyMessage(0x0100, 0x1B)
                || MicroDialWindow.IsEscapeDismissKeyMessage(0x0101, 0x1B)
                || MicroDialWindow.IsEscapeDismissKeyMessage(0x0100, 0x0D))
            {
                throw new InvalidOperationException("Esc キー押下判定が期待と異なります。");
            }

            // WS_EX_NOACTIVATE 付き Popup への SetForegroundWindow は即閉鎖を招くためコードに残っていないこと。
            var dialPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel", "Services", "MicroDialWindow.cs"));
            var dialCode = System.IO.File.ReadAllText(dialPath);
            if (dialCode.Contains("SetForegroundWindow(", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MicroDialWindow.cs に即閉鎖を引き起こす SetForegroundWindow 呼び出しが残っています。");
            }

            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var res = dial.VerifyContextMenuOutsideClickDismissalForTesting();
                if (res.DismissedOnParentMenuClick)
                {
                    throw new InvalidOperationException("親メニュー内クリックで誤ってメニューが閉じました。");
                }

                if (res.DismissedOnSubmenuClick)
                {
                    throw new InvalidOperationException("展開中のサブメニュー内クリックで誤ってメニューが閉じました。");
                }

                if (!res.DismissedOnOutsideClick || !res.MenuClearedAfterOutsideClick)
                {
                    throw new InvalidOperationException("メニュー外クリックで右クリックメニューが閉じていません。");
                }

                if (!res.MenuClearedAfterHideWithPopups)
                {
                    throw new InvalidOperationException("HideWithPopups 実行後に右クリックメニュー追跡状態が解除されていません。");
                }

                if (!res.HooksInstallAndUninstallCleanly)
                {
                    throw new InvalidOperationException("WH_MOUSE_LL / WH_KEYBOARD_LL フックの登録・解除が正常に完了しませんでした。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestStandaloneExitConfirmWindowStructureAndPlacement(List<string> failures)
    {
        RunOnStaThread(failures, "縮小・極小用単独終了確認カードウィンドウの構成・中央配置・クランプ検証", () =>
        {
            // 中央配置と作業領域端クランプの純粋計算検証
            var workArea = new WindowArranger.WindowBounds(0, 0, 1920, 1040);
            var microAnchor = new Rect(902, 462, 116, 116);
            var dialogSize = new Size(396, 206);
            var (centerLeft, centerTop) = AppExitConfirmWindow.ComputeCenteredPositionPx(microAnchor, dialogSize, workArea);
            if (Math.Abs(centerLeft - (960 - 198)) > 2 || Math.Abs(centerTop - (520 - 103)) > 2)
            {
                throw new InvalidOperationException($"極小ダイヤル基準の中央配置がずれています ({centerLeft}, {centerTop})。");
            }

            // 画面左上隅アンカーでも作業領域外へはみ出さないこと
            var cornerAnchor = new Rect(10, 10, 116, 116);
            var (clampedLeft, clampedTop) = AppExitConfirmWindow.ComputeCenteredPositionPx(cornerAnchor, dialogSize, workArea);
            if (clampedLeft < workArea.Left || clampedTop < workArea.Top)
            {
                throw new InvalidOperationException($"端配置クランプが効いていません ({clampedLeft}, {clampedTop})。");
            }

            var confirmed = 0;
            var canceled = 0;
            var confirmWin = new AppExitConfirmWindow(microAnchor, workArea, () => confirmed++, () => canceled++);
            try
            {
                var info = confirmWin.VerifyOffscreenForTesting();
                if (!info.AllowsTransparencyValue || !info.CancelIsDefaultCancelKey)
                {
                    throw new InvalidOperationException("AppExitConfirmWindow の透過設定または既定キャンセル設定が不正です。");
                }

                // 余計な親パネル背景を持たず、カード単体（幅 ~396 DIP、高さ ~180..240 DIP）に収まること
                if (!(info.DesiredCardSizeDip.Width is >= 380 and <= 420
                    && info.DesiredCardSizeDip.Height is >= 170 and <= 245))
                {
                    throw new InvalidOperationException(
                        $"単独終了確認カードの寸法が想定範囲外です ({info.DesiredCardSizeDip.Width:0}x{info.DesiredCardSizeDip.Height:0})。");
                }

                confirmWin.SimulateConfirmClickForTesting();
                confirmWin.SimulateConfirmClickForTesting();
                if (confirmed != 1 || canceled != 0)
                {
                    throw new InvalidOperationException($"終了確定コールバックが 1 回だけ呼ばれませんでした (confirm={confirmed}, cancel={canceled})。");
                }
            }
            finally
            {
                confirmWin.CloseSilently();
            }

            var cancelWin = new AppExitConfirmWindow(microAnchor, workArea, () => confirmed++, () => canceled++);
            try
            {
                cancelWin.SimulateCancelClickForTesting();
                if (canceled != 1)
                {
                    throw new InvalidOperationException($"キャンセルコールバックが 1 回だけ呼ばれませんでした (cancel={canceled})。");
                }
            }
            finally
            {
                cancelWin.CloseSilently();
            }
        });
    }

    private static void TestMicroDialCenterMenuHasLaunchAllAndCloseAll(List<string> failures)
    {
        RunOnStaThread(failures, "極小中央ボタンの右クリックメニューに一括起動・すべて閉じるが並ぶことの検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                // 管理中ウィンドウが無い間は「すべて閉じる」を押せない
                var centerItems = dial.GetContextMenuItemsForTesting(null);
                if (centerItems.Count < 2
                    || centerItems[0].Header != TurtleAIQuartetHub.Panel.MainWindow.MicroLaunchAllMenuHeader
                    || centerItems[1].Header != TurtleAIQuartetHub.Panel.MainWindow.MicroCloseAllMenuHeader)
                {
                    throw new InvalidOperationException(
                        $"中央メニューの先頭が一括起動・すべて閉じるになっていません ({string.Join(" / ", centerItems.Select(i => i.Header))})。");
                }

                if (centerItems[1].IsEnabled)
                {
                    throw new InvalidOperationException("管理中ウィンドウが無いのに「すべて閉じる」が押せる状態です。");
                }

                slots[0].WindowHandle = new IntPtr(0x1234);
                var withWindowItems = dial.GetContextMenuItemsForTesting(null);
                if (!withWindowItems[1].IsEnabled)
                {
                    throw new InvalidOperationException("管理中ウィンドウがあるのに「すべて閉じる」が押せません。");
                }

                // 扇（スロット）上のメニューには一括操作を混ぜない
                var sectorHeaders = dial.GetContextMenuItemsForTesting(0).Select(i => i.Header).ToList();
                if (sectorHeaders.Contains(TurtleAIQuartetHub.Panel.MainWindow.MicroLaunchAllMenuHeader) || sectorHeaders.Contains(TurtleAIQuartetHub.Panel.MainWindow.MicroCloseAllMenuHeader))
                {
                    throw new InvalidOperationException("扇の右クリックメニューに一括起動・すべて閉じるが混ざっています。");
                }
            }
            finally
            {
                dial.Close();
            }

            // 「すべて閉じる」の単独確認カードは標準表示の確認と同じ文言で、確定は 1 回だけ届く
            var workArea = new WindowArranger.WindowBounds(0, 0, 1920, 1040);
            var anchor = new Rect(902, 462, 116, 116);
            var confirmed = 0;
            var canceled = 0;
            var closeAllWin = new AppExitConfirmWindow(
                anchor,
                workArea,
                () => confirmed++,
                () => canceled++,
                AppExitConfirmWindow.ConfirmCardContent.CloseAll(3));
            try
            {
                var texts = closeAllWin.CollectCardTextsForTesting();
                string[] expected = ["すべて閉じていいですか？", "3個の管理中ウィンドウを閉じます。", "キャンセル", "閉じる"];
                if (expected.Any(text => !texts.Contains(text)) || texts.Contains("アプリを終了しますか？"))
                {
                    throw new InvalidOperationException($"すべて閉じるの確認カードの文言が不正です ({string.Join(" / ", texts)})。");
                }

                closeAllWin.SimulateConfirmClickForTesting();
                closeAllWin.SimulateConfirmClickForTesting();
                if (confirmed != 1 || canceled != 0)
                {
                    throw new InvalidOperationException($"すべて閉じるの確定が 1 回だけ届きませんでした (confirm={confirmed}, cancel={canceled})。");
                }
            }
            finally
            {
                closeAllWin.CloseSilently();
            }

            // 既定（引数なし）は従来どおり終了確認の文言のまま
            var exitWin = new AppExitConfirmWindow(anchor, workArea, () => { }, () => { });
            try
            {
                var exitTexts = exitWin.CollectCardTextsForTesting();
                if (!exitTexts.Contains("アプリを終了しますか？") || !exitTexts.Contains("終了する"))
                {
                    throw new InvalidOperationException($"既定の終了確認カードの文言が変わっています ({string.Join(" / ", exitTexts)})。");
                }
            }
            finally
            {
                exitWin.CloseSilently();
            }
        });

        // 本番のメニュー（MainWindow 側）も、扇以外で一括操作を出し、標準表示と同じ処理へつながること
        var panelDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
        var slotMenuCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.SlotContextMenu.cs"));
        var mainCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"));
        if (!slotMenuCs.Contains("AddMicroBulkActionMenuItems(menu);", StringComparison.Ordinal)
            || !slotMenuCs.Contains("await LaunchAllMissingAsync()", StringComparison.Ordinal)
            || !slotMenuCs.Contains("RequestCloseAllFromMicroDial", StringComparison.Ordinal))
        {
            failures.Add("極小中央メニューの一括起動・すべて閉じるが標準表示と同じ処理（LaunchAllMissingAsync / 確認付きすべて閉じる）につながっていません。");
        }

        var requestStart = mainCs.IndexOf("private void RequestCloseAllFromMicroDial()", StringComparison.Ordinal);
        var requestEnd = requestStart < 0 ? -1 : mainCs.IndexOf("private void CloseStandaloneCloseAllConfirmDialog()", requestStart, StringComparison.Ordinal);
        if (requestStart < 0 || requestEnd <= requestStart)
        {
            failures.Add("MainWindow.xaml.cs に RequestCloseAllFromMicroDial が見つかりません。");
            return;
        }

        var requestBody = mainCs[requestStart..requestEnd];
        if (!requestBody.Contains("ConfirmCardContent.CloseAll(", StringComparison.Ordinal)
            || !requestBody.Contains("await CloseAllManagedWindowsAsync();", StringComparison.Ordinal)
            || requestBody.Contains("CloseAllConfirmOverlay", StringComparison.Ordinal))
        {
            failures.Add("極小表示のすべて閉じるが、単独確認カード → CloseAllManagedWindowsAsync の経路になっていません。");
        }
    }

    private static void TestCompactAndMicroExitConfirmAvoidMainWindowResizeAndUncloak(List<string> failures)
    {
        var panelDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
        var mainCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"));
        var startupCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.StartupAnimation.cs"));

        var showStart = mainCs.IndexOf("private void ShowAppExitConfirmDialog()", StringComparison.Ordinal);
        var showEnd = mainCs.IndexOf("private void ShowStandaloneExitConfirmDialog()", StringComparison.Ordinal);
        if (showStart < 0 || showEnd <= showStart)
        {
            failures.Add("MainWindow.xaml.cs に ShowAppExitConfirmDialog / ShowStandaloneExitConfirmDialog が見つかりません。");
            return;
        }

        var showBody = mainCs[showStart..showEnd];
        if (!showBody.Contains("if (!IsStandardMode)", StringComparison.Ordinal)
            || !showBody.Contains("ShowStandaloneExitConfirmDialog();", StringComparison.Ordinal)
            || showBody.Contains("SuspendMicroDialForMainOverlay", StringComparison.Ordinal))
        {
            failures.Add("縮小・極小モードの終了確認が本体非変形・非露出の単独ウィンドウ経路になっていません。");
        }

        if (!startupCs.Contains("CaptureCoreCircleImage()", StringComparison.Ordinal)
            || !startupCs.Contains("MicroDialWindow.CoreRadiusDip", StringComparison.Ordinal))
        {
            failures.Add("極小モードの終了消散アニメーションが真円オービット・ダイヤル対応になっていません。");
        }
    }

    private static void TestMicroDialCaptureCoreCircleImage(List<string> failures)
    {
        RunOnStaThread(failures, "円形コアキャプチャ画像の生成検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                _ = dial.VerifyOffscreenStateForTesting();
                var bmp = dial.CaptureCoreCircleImage();
                if (bmp.PixelWidth < 100 || bmp.PixelHeight < 100 || !bmp.IsFrozen)
                {
                    throw new InvalidOperationException($"円形コアキャプチャ画像が不正です ({bmp.PixelWidth}x{bmp.PixelHeight}, frozen={bmp.IsFrozen})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialCaptureCoreCircleImageCenteredWithoutVerticalClip(List<string> failures)
    {
        RunOnStaThread(failures, "円形コアキャプチャ画像の上下対称・枠切れ無し・四隅透過検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                _ = dial.VerifyOffscreenStateForTesting();
                var bmp = dial.CaptureCoreCircleImage();
                var w = bmp.PixelWidth;
                var h = bmp.PixelHeight;
                var stride = w * 4;
                var pixels = new byte[stride * h];
                bmp.CopyPixels(pixels, stride, 0);

                byte AlphaAt(int x, int y) => pixels[(y * stride) + (x * 4) + 3];

                // 四隅（円外）がソフト影ゴミ無しの完全透明であること
                if (AlphaAt(0, 0) != 0 || AlphaAt(w - 1, 0) != 0 || AlphaAt(0, h - 1) != 0 || AlphaAt(w - 1, h - 1) != 0)
                {
                    throw new InvalidOperationException("円形コアキャプチャの四隅にソフト影などの不透明ゴミが混入しています。");
                }

                // 中央縦ラインの上端・下端の不透明ピクセル開始位置が対称（ズレ 1px 以内）であり、
                // かつ円の上端・下端が端から 2px 以内でしっかり収まっていること（旧バグ: 上端が 3px 切れて下端に 3px 余白）
                var cx = w / 2;
                var topEdge = Enumerable.Range(0, h).FirstOrDefault(y => AlphaAt(cx, y) > 128, -1);
                var bottomEdge = Enumerable.Range(0, h).Reverse().FirstOrDefault(y => AlphaAt(cx, y) > 128, -1);
                var topMargin = topEdge;
                var bottomMargin = (h - 1) - bottomEdge;
                if (topEdge < 0 || bottomEdge < 0 || topMargin > 2 || bottomMargin > 2 || Math.Abs(topMargin - bottomMargin) > 1)
                {
                    throw new InvalidOperationException(
                        $"円形コアキャプチャの上下端が非対称または切れています (h={h}, topEdge={topEdge}, bottomEdge={bottomEdge})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialCoreShadowConcentricWithCore(List<string> failures)
    {
        RunOnStaThread(failures, "本体円背面のソフト影が本体円と同心（上下左右の縁取り幅が均一）であることの検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyCoreShadowConcentricForTesting();

                // 旧バグ: 影を下へ 2〜3 DIP ずらしていたため、縁取りが上は細く（外側 5 DIP）下は太く（外側 11 DIP）見えた。
                static void Check(string name, (double Top, double Bottom, double Left, double Right) m)
                {
                    const double tolerance = 0.01;
                    if (m.Top <= 0
                        || Math.Abs(m.Top - m.Bottom) > tolerance
                        || Math.Abs(m.Left - m.Right) > tolerance
                        || Math.Abs(m.Top - m.Left) > tolerance)
                    {
                        throw new InvalidOperationException(
                            $"{name}の縁取り幅が上下左右で均一ではありません (top={m.Top:0.00}, bottom={m.Bottom:0.00}, left={m.Left:0.00}, right={m.Right:0.00})。");
                    }
                }

                Check("外側ソフト影", info.OuterShadowMargins);
                Check("内側ソフト影", info.InnerShadowMargins);
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialNonActivatingMenuClickAndJumpGuards(List<string> failures)
    {
        RunOnStaThread(failures, "極小右クリックメニューの非アクティブクリック閉鎖・サブメニュー展開・ジャンプ抑止検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var res = dial.VerifyNonActivatingMenuBehaviorsForTesting();
                if (res.LeafClickCount != 1 || !res.MenuClosedAfterLeafClick)
                {
                    throw new InvalidOperationException(
                        $"非アクティブ ContextMenu のリーフ項目クリック後に 1 回実行＆自動閉鎖されませんでした (clicks={res.LeafClickCount}, closed={res.MenuClosedAfterLeafClick})。");
                }

                if (res.SubmenuLeafClickCount != 1 || !res.MenuClosedAfterSubmenuLeafClick)
                {
                    throw new InvalidOperationException(
                        $"非アクティブ ContextMenu のサブメニュー項目クリック後に 1 回実行＆自動閉鎖されませんでした (clicks={res.SubmenuLeafClickCount}, closed={res.MenuClosedAfterSubmenuLeafClick})。");
                }
            }
            finally
            {
                dial.Close();
            }

            // 本番コードで BuildMicroDialContextMenu・GitHub・エクスプローラーの各経路が
            // フォーカス再主張抑止・リンク再解決・有効な RoutedEvent 付き RoutedEventArgs を備えていること
            var panelDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
            var slotMenuCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.SlotContextMenu.cs"));
            var mainCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"));

            if (!slotMenuCs.Contains("slot.RefreshWorkspaceLinks();", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MainWindow.SlotContextMenu.cs でメニュー構築前に RefreshWorkspaceLinks() を呼んでいません。");
            }

            // 引数なし new RoutedEventArgs() に e.Handled = true を設定すると WPF が InvalidOperationException を投げるため、
            // Panel プロジェクト全体に引数なし new RoutedEventArgs() が 1 件も残っていないことを保証する。
            foreach (var csFile in System.IO.Directory.EnumerateFiles(panelDir, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                if (csFile.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                    || csFile.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                    || csFile.Contains("\\.build-tmp\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = System.IO.File.ReadAllText(csFile);
                if (code.Contains("new RoutedEventArgs()", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{System.IO.Path.GetFileName(csFile)} に RoutedEvent=null の引数なし new RoutedEventArgs() が残っています（e.Handled = true で例外になります）。");
                }
            }

            // MenuItem.ClickEvent 付き RoutedEventArgs は e.Handled = true が例外なく通ること
            var validMenuArgs = new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent);
            validMenuArgs.Handled = true;
            if (!validMenuArgs.Handled)
            {
                throw new InvalidOperationException("MenuItem.ClickEvent 付き RoutedEventArgs で Handled=true を設定できませんでした。");
            }

            // 非アクティブ ContextMenu に手動 PreviewMouseLeftButtonUp → Click 再発火を入れると
            // WPF 標準の InvokeClickAfterRender と競合してタブが 2 つ開くなどの二重実行を起こすため、
            // AttachNonActivatingMenuItems 内に手動 RaiseEvent(ClickEvent) が存在しないことも保証する。
            var dialCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "Services", "MicroDialWindow.cs"));
            var attachStart = dialCs.IndexOf("private void AttachNonActivatingMenuItems(", StringComparison.Ordinal);
            var attachEnd = dialCs.IndexOf("private static void CloseSiblingSubmenus(", StringComparison.Ordinal);
            if (attachStart < 0 || attachEnd <= attachStart)
            {
                throw new InvalidOperationException("MicroDialWindow.cs に AttachNonActivatingMenuItems が見つかりません。");
            }

            if (dialCs[attachStart..attachEnd].Contains("RaiseEvent(", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AttachNonActivatingMenuItems 内に二重実行を引き起こす手動 RaiseEvent が残っています。");
            }

            var githubStart = mainCs.IndexOf("private void OpenSlotGitHubButton_Click(", StringComparison.Ordinal);
            var githubEnd = mainCs.IndexOf("private void PullRequestInboxButton_Click(", StringComparison.Ordinal);
            if (githubStart < 0 || githubEnd <= githubStart
                || !mainCs[githubStart..githubEnd].Contains("SuppressFocusedSlotReassertForPanelInput();", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OpenSlotGitHubButton_Click にフォーカス再主張抑止がありません。");
            }
        });
    }

    private static void TestMicroDialCenterButtonSupportsDragAndSingleClickToggle(List<string> failures)
    {
        RunOnStaThread(failures, "極小ダイヤル中央ボタンからのドラッグ移動と単クリック切替の両立検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var toggleCount = 0;
            var movedCount = 0;
            var dial = new MicroDialWindow(
                slots,
                ghostOpacityEnabled: true,
                getAuxApplications: () => [],
                onSlotActivated: _ => { },
                onStoredPanelDropped: (_, _) => { },
                onSlotSwapped: (_, _) => { },
                onToggleVisibility: () => toggleCount++,
                onToggleStoredOverview: () => { },
                onPinTop: () => { },
                onSendBack: () => { },
                onLaunchAuxApp: _ => { },
                onSwitchStandard: () => { },
                onSwitchCompact: () => { },
                onMinimize: () => { },
                onRequestClose: () => { },
                onGhostOpacityChanged: (_, _) => { },
                onRecenterRequested: () => { },
                onCoreCenterMoved: _ => movedCount++);
            try
            {
                var res = dial.VerifyCenterButtonDragAndClickForTesting();
                if (!res.DragMovedWindowWithoutToggle || movedCount < 1)
                {
                    throw new InvalidOperationException($"中央ボタンからのドラッグ移動が機能していません (moved={movedCount})。");
                }

                // ドラッグ直後の誤クリックは抑止され、通常単クリックの 1 回だけトグルが発火すること
                if (toggleCount != 1)
                {
                    throw new InvalidOperationException($"中央ボタンのドラッグ抑止／単クリックトグル回数が期待（1）と異なります ({toggleCount})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialInnerWorkingArcBrighterAndThicker(List<string> failures)
    {
        RunOnStaThread(failures, "実行中ホタル点滅（中央内側アーク）の視認性・太さ・明度検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyWorkingArcAndIdleOpacityPolicyForTesting();
                if (!(info.OuterThickness >= 5.0 && info.MiddleThickness >= 3.0 && info.InnerThickness >= 1.5))
                {
                    throw new InvalidOperationException(
                        $"実行中内側アークの太さが視認性改善基準を満たしていません (outer={info.OuterThickness:0.00}, mid={info.MiddleThickness:0.00}, inner={info.InnerThickness:0.00})。");
                }

                if (!(info.CoreColor.G >= 0xF5 && info.CoreColor.R >= 0xDF && info.MinFireflyOpacity >= 0.24))
                {
                    throw new InvalidOperationException(
                        $"実行中内側アークの明るさ・点滅下限が基準を満たしていません (core={info.CoreColor}, minOpacity={info.MinFireflyOpacity:0.00})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialOuterWorkingOrbitRunsOnlyWhileAnySlotWorking(List<string> failures)
    {
        RunOnStaThread(failures, "実行中の外周 LED 周回（どれか 1 つでも実行中のときだけ・外枠線上・キャプチャ対象外）検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyCoreWorkingOrbitForTesting();

                // _coreHost の内側へ入れると VisualBrush(_coreHost) のキャプチャ／サムネイルの円がずれて切れる。
                if (!info.HostedOutsideCoreHostInFront)
                {
                    throw new InvalidOperationException("外周 LED 周回が _coreHost の外側（_dialRoot の前面）に置かれていません。");
                }

                if (!info.CollapsedWhenNoneWorking || !info.CollapsedAfterAllStopped)
                {
                    throw new InvalidOperationException(
                        $"実行中のスロットが 1 つも無いのに外周 LED 周回が出ています (none={info.CollapsedWhenNoneWorking}, afterStop={info.CollapsedAfterAllStopped})。");
                }

                if (!info.VisibleWhenOneWorking || !info.VisibleWhenSeveralWorking)
                {
                    throw new InvalidOperationException(
                        $"実行中のスロットがあるのに外周 LED 周回が出ていません (one={info.VisibleWhenOneWorking}, several={info.VisibleWhenSeveralWorking})。");
                }

                if (!info.CollapsedWhileAssignWaiting)
                {
                    throw new InvalidOperationException("控え入替待ちの間も外周 LED 周回が動いています（内側ホタル点滅と同じく止めること）。");
                }

                if (info.MaxCenterlineRadiusError > 0.05 || info.CenterOffsetFromCore > 0.05)
                {
                    throw new InvalidOperationException(
                        $"外周 LED 周回が本体円の外枠線の中心線上を同心で回っていません (radiusError={info.MaxCenterlineRadiusError:0.000}, centerOffset={info.CenterOffsetFromCore:0.000})。");
                }

                // ふわっとした外側の光まで含めても、背面のソフト影の内側に収まり、展開時の外周ボタンへ届かないこと。
                if (info.MaxGlowReachFromCenter <= MicroDialWindow.CoreRadiusDip
                    || info.MaxGlowReachFromCenter > info.ShadowOuterRadius
                    || info.MaxGlowReachFromCenter >= info.SatelliteInnerRadius)
                {
                    throw new InvalidOperationException(
                        $"外周 LED 周回の光の届く範囲が想定外です (reach={info.MaxGlowReachFromCenter:0.00}, shadow={info.ShadowOuterRadius:0.00}, satellite={info.SatelliteInnerRadius:0.00})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialIdleOpacityThinWhenFocusedOrWorkingAndDenseOnlyWhenDone(List<string> failures)
    {
        RunOnStaThread(failures, "非ホバー時の透過方針（フォーカス中・実行中は薄く、完了・再ホバー時は濃く）検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyWorkingArcAndIdleOpacityPolicyForTesting();
                if (Math.Abs(info.NormalIdleOpacity - 0.30) > 0.01)
                {
                    throw new InvalidOperationException($"通常・非ホバー時の透過率が 0.30 ではありません ({info.NormalIdleOpacity:0.00})。");
                }

                if (Math.Abs(info.FocusedIdleOpacity - 0.30) > 0.01)
                {
                    throw new InvalidOperationException($"フォーカス中・非ホバー時に薄くなっていません ({info.FocusedIdleOpacity:0.00})。");
                }

                if (Math.Abs(info.WorkingIdleOpacity - 0.30) > 0.01)
                {
                    throw new InvalidOperationException($"AI 実行中・非ホバー時に薄くなっていません ({info.WorkingIdleOpacity:0.00})。");
                }

                if (info.DoneIdleOpacity < 0.85)
                {
                    throw new InvalidOperationException($"AI 完了・非ホバー時に濃く表示されていません ({info.DoneIdleOpacity:0.00})。");
                }

                if (info.HoveredOpacity < 0.99)
                {
                    throw new InvalidOperationException($"マウスオーバー時に濃く表示されていません ({info.HoveredOpacity:0.00})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialRadialLabelSymmetry(List<string> failures)
    {
        RunOnStaThread(failures, "極小ダイヤルの放射状対称ラベル配置（アルファベット中央寄り・タイトル外周寄り）検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyRadialLabelSymmetryForTesting();
                if (!info.TopRowTitleAboveKey)
                {
                    throw new InvalidOperationException("上段（A・B）でタイトルがアルファベットの上側（外周寄り）に配置されていません。");
                }

                if (!info.BottomRowKeyAboveTitle)
                {
                    throw new InvalidOperationException("下段（C・D）でアルファベットがタイトルの上側（中央寄り）に配置されていません。");
                }

                if (info.KeyCenterDistanceDeltaY > 1.0 || info.TitleCenterDistanceDeltaY > 1.0)
                {
                    throw new InvalidOperationException(
                        $"上下段のアルファベット・タイトルの中央からの距離が対称ではありません (keyDelta={info.KeyCenterDistanceDeltaY:0.00}, titleDelta={info.TitleCenterDistanceDeltaY:0.00})。");
                }

                if (info.LeftRightOffsetDeltaX > 0.5)
                {
                    throw new InvalidOperationException(
                        $"上下段の横位置オフセットが揃っていません (deltaX={info.LeftRightOffsetDeltaX:0.00})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestCompactAndMicroStoredOverviewScrollableWithoutScrollBar(List<string> failures)
    {
        // 1. 折りたたみ時のビューポート高さ計算（実測値・区切り線あり・未計測フォールバック）
        var measuredWithDivider = TurtleAIQuartetHub.Panel.MainWindow.ComputeCompactOverviewViewportHeight([55, 75, 55, 55, 55], 5);
        if (Math.Abs(measuredWithDivider - 295.0) > 0.01)
        {
            failures.Add($"区切り線込み 5 行分の折りたたみ高さ計算が期待（295）と異なります ({measuredWithDivider:0.0})。");
        }

        var fallbackHeight = TurtleAIQuartetHub.Panel.MainWindow.ComputeCompactOverviewViewportHeight([], 5);
        if (Math.Abs(fallbackHeight - 275.0) > 0.01)
        {
            failures.Add($"未計測時の 5 行分フォールバック高さが期待（275）と異なります ({fallbackHeight:0.0})。");
        }

        // 2. マウスホイール上下スクロールの計算（下へ 1 ノッチ＝+55 DIP、上へ 1 ノッチ＝-55 DIP、上下端クランプ）
        var downOne = TurtleAIQuartetHub.Panel.MainWindow.ComputeStoredOverviewNextScrollOffset(0, 275, wheelDelta: -120);
        var upOne = TurtleAIQuartetHub.Panel.MainWindow.ComputeStoredOverviewNextScrollOffset(downOne, 275, wheelDelta: +120);
        var clampedBottom = TurtleAIQuartetHub.Panel.MainWindow.ComputeStoredOverviewNextScrollOffset(260, 275, wheelDelta: -120);
        var clampedTop = TurtleAIQuartetHub.Panel.MainWindow.ComputeStoredOverviewNextScrollOffset(10, 275, wheelDelta: +120);
        if (Math.Abs(downOne - 55.0) > 0.01 || Math.Abs(upOne - 0.0) > 0.01 || Math.Abs(clampedBottom - 275.0) > 0.01 || Math.Abs(clampedTop - 0.0) > 0.01)
        {
            failures.Add($"控え一覧ホイールスクロール計算が期待と異なります (down={downOne}, up={upOne}, bottom={clampedBottom}, top={clampedTop})。");
        }

        // 3. 縮小・極小表示では後半行を Collapsed で隠さず Visible のまま保持し、
        //    ScrollBarVisibility.Hidden でスクロールバー非表示＋PreviewMouseWheel でスクロール可能であること
        var panelDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
        var mainCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"));
        var mainXaml = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml"));

        var updateStart = mainCs.IndexOf("private void UpdateStoredOverviewRows()", StringComparison.Ordinal);
        var updateEnd = mainCs.IndexOf("internal static double ComputeCompactOverviewViewportHeight(", StringComparison.Ordinal);
        if (updateStart < 0 || updateEnd <= updateStart)
        {
            failures.Add("MainWindow.xaml.cs に UpdateStoredOverviewRows が見つかりません。");
            return;
        }

        var updateBody = mainCs[updateStart..updateEnd];
        if (!updateBody.Contains("ScrollBarVisibility.Hidden", StringComparison.Ordinal)
            || !updateBody.Contains("StoredOverviewScrollViewer.MaxHeight =", StringComparison.Ordinal)
            || updateBody.Contains("index >= CompactOverviewVisibleRows ? Visibility.Collapsed", StringComparison.Ordinal))
        {
            failures.Add("UpdateStoredOverviewRows がスクロールバー非表示＋全行保持の MaxHeight 制限方式になっていません。");
        }

        if (!mainXaml.Contains("PreviewMouseWheel=\"StoredOverviewScrollViewer_PreviewMouseWheel\"", StringComparison.Ordinal))
        {
            failures.Add("MainWindow.xaml の控え一覧に PreviewMouseWheel ハンドラが設定されていません。");
        }
    }

    private static void TestMicroDialStoredAssignAndHiddenStateTransitions(List<string> failures)
    {
        RunOnStaThread(failures, "控え入替待ち・一括非表示状態遷移の検証", () =>
        {
            var slots = CreateSampleSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                _ = dial.VerifyOffscreenStateForTesting();
                var stored = new StoredPanelSlot(1)
                {
                    PanelTitle = "Docs-Portal",
                    WorkspacePath = @"c:\work\docs-portal"
                };

                dial.SetStoredOverviewOpen(true);
                dial.SetPendingStoredPanel(stored);
                if (!dial.IsExpanded)
                {
                    throw new InvalidOperationException("控え一覧展開中・入替待ち中にダイヤルが展開状態を維持できませんでした。");
                }

                dial.SetWindowsHiddenState(true);
                dial.SetWindowsHiddenState(false);
                dial.SetPendingStoredPanel(null);
                dial.SetStoredOverviewOpen(false);
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialModeButtonIconUsesCircularDialVector(List<string> failures)
    {
        RunOnStaThread(failures, "極小表示切替ボタンの円形ダイヤルベクターアイコン検証", () =>
        {
            var icon = TurtleAIQuartetHub.Panel.MainWindow.CreateMicroDialModeIcon();
            if (icon.Width < 12 || icon.Height < 12)
            {
                throw new InvalidOperationException($"アイコン寸法が不正です ({icon.Width}x{icon.Height})。");
            }

            var ellipses = icon.Children.OfType<System.Windows.Shapes.Ellipse>().Count();
            var paths = icon.Children.OfType<System.Windows.Shapes.Path>().Count();
            if (ellipses != 2 || paths != 1)
            {
                throw new InvalidOperationException($"外円・中央円・4分割スポークの構成が期待と異なります (ellipses={ellipses}, paths={paths})。");
            }
        });
    }

    private static void TestStandardAndCompactModeIconsUseAbsoluteVectorShapes(List<string> failures)
    {
        RunOnStaThread(failures, "標準（フル4タイル）・縮小（極細横長バー）の絶対ベクターアイコン検証", () =>
        {
            var standard1 = TurtleAIQuartetHub.Panel.MainWindow.CreateStandardModeIcon();
            var standard2 = TurtleAIQuartetHub.Panel.MainWindow.CreateStandardModeIcon();
            if (ReferenceEquals(standard1, standard2))
            {
                throw new InvalidOperationException("CreateStandardModeIcon がボタン間で共有不可の同一インスタンスを返しています。");
            }

            var tiles = standard1.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
            if (tiles.Count != 4 || tiles.Any(t => Math.Abs(t.Width - t.Height) > 0.1 || t.Width < 4.5))
            {
                throw new InvalidOperationException($"標準モードアイコンのフル 4 タイル構成が期待と異なります (tiles={tiles.Count})。");
            }

            var compact1 = TurtleAIQuartetHub.Panel.MainWindow.CreateCompactModeIcon();
            var compact2 = TurtleAIQuartetHub.Panel.MainWindow.CreateCompactModeIcon();
            if (ReferenceEquals(compact1, compact2))
            {
                throw new InvalidOperationException("CreateCompactModeIcon がボタン間で共有不可の同一インスタンスを返しています。");
            }

            var bars = compact1.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
            var dividers = compact1.Children.OfType<System.Windows.Shapes.Path>().ToList();
            if (bars.Count != 1 || dividers.Count != 1)
            {
                throw new InvalidOperationException($"縮小モードアイコンの極細バー＋中央仕切り構成が期待と異なります (bars={bars.Count}, dividers={dividers.Count})。");
            }

            var bar = bars[0];
            if (!(bar.Width >= 11.0 && bar.Height <= 6.5 && bar.Width >= bar.Height * 1.9))
            {
                throw new InvalidOperationException($"縮小モードアイコンが横長の極細バー形状ではありません ({bar.Width:0.0}x{bar.Height:0.0})。");
            }

            // タイトルバー XAML・コード・極小ダイヤルに旧斜め矢印（E740 / E73F）が残っていないことも確認する。
            var panelDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
            string[] checkedFiles =
            [
                System.IO.Path.Combine(panelDir, "MainWindow.xaml"),
                System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"),
                System.IO.Path.Combine(panelDir, "Services", "MicroDialWindow.cs")
            ];
            foreach (var file in checkedFiles)
            {
                var text = System.IO.File.ReadAllText(file);
                if (text.Contains("E740", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("E73F", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{System.IO.Path.GetFileName(file)} に旧相対矢印アイコン（E740 / E73F）が残っています。");
                }
            }
        });
    }

    private static void TestMicroDialTopArcUsesAbsoluteStandardAndCompactVectorIcons(List<string> failures)
    {
        RunOnStaThread(failures, "極小ダイヤル上弧の標準・縮小絶対アイコンと明色Foreground検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyTopModeSatellitesForTesting();
                if (info.StandardTileCount != 4)
                {
                    throw new InvalidOperationException($"極小ダイヤル上弧の標準ボタンがフル 4 タイルではありません (tiles={info.StandardTileCount})。");
                }

                if (info.CompactRectCount != 1 || info.CompactDividerCount != 1)
                {
                    throw new InvalidOperationException($"極小ダイヤル上弧の縮小ボタンが極細バー構成ではありません (rects={info.CompactRectCount}, dividers={info.CompactDividerCount})。");
                }

                // 暗い丸ボタン背景上でベクター線が沈まないよう明るい Foreground（#EEF4EF 系）が設定されていること。
                if (info.StandardButtonForeground.R < 0xD0 || info.CompactButtonForeground.R < 0xD0)
                {
                    throw new InvalidOperationException(
                        $"極小ダイヤル上弧の標準/縮小ボタン Foreground が暗すぎます (std={info.StandardButtonForeground}, cmp={info.CompactButtonForeground})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialBottomArcOrderAndStoredPillClearance(List<string> failures)
    {
        RunOnStaThread(failures, "下弧ボタンの左右順（AI左・控え右）と控えピルの隙間均等化検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyRefinedUiForTesting();
                var storedCenterX = info.StoredPillRect.Left + (info.StoredPillRect.Width / 2.0);
                if (!(info.AuxAiCenter.X < info.PinTopCenter.X
                    && info.PinTopCenter.X < info.SendBackCenter.X
                    && info.SendBackCenter.X < storedCenterX))
                {
                    throw new InvalidOperationException(
                        $"下弧ボタンの左右順が期待（AI < 前 < 背 < 控え）と異なります (AI={info.AuxAiCenter.X:0.0}, 前={info.PinTopCenter.X:0.0}, 背={info.SendBackCenter.X:0.0}, 控え={storedCenterX:0.0})。");
                }

                if (!(info.StoredPillMinClearanceFromCore is >= 8.0 and <= 10.5))
                {
                    throw new InvalidOperationException(
                        $"控えピルと中央円の最短隙間が他サテライト（9.0 DIP）と一致しません (clearance={info.StoredPillMinClearanceFromCore:0.00} DIP)。");
                }

                if (info.StoredPillRect.Right > MicroDialWindow.StageSizeDip - 2.0
                    || info.StoredPillRect.Bottom > MicroDialWindow.StageSizeDip - 2.0)
                {
                    throw new InvalidOperationException(
                        $"控えピルがステージ領域内に収まっていません (right={info.StoredPillRect.Right:0.0}, bottom={info.StoredPillRect.Bottom:0.0}, stage={MicroDialWindow.StageSizeDip:0.0})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialDarkContextMenuAndStatusVisualContrast(List<string> failures)
    {
        RunOnStaThread(failures, "右クリックメニューのダークスタイル・左揃えと起動/停止視認性検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyRefinedUiForTesting();
                if (!info.DialContextMenuOverridesDefaultStyle)
                {
                    throw new InvalidOperationException("右クリックメニューがカスタムダークテンプレート（OverridesDefaultStyle=True）になっていません。");
                }

                if (!info.GhostCheckedHeaderMatchesUncheckedHeader)
                {
                    throw new InvalidOperationException("透過チェック ON/OFF 切替時にメニュー文言またはアイコン列の扱いが揃っていません。");
                }

                // 起動済み(Ready)=緑発光、停止中(Missing)=非発光グレー、起動中(Launching)=黄
                if (!(info.ReadySectorKeyColor.G > info.ReadySectorKeyColor.R + 40
                    && info.ReadySectorKeyColor.G > info.ReadySectorKeyColor.B + 20))
                {
                    throw new InvalidOperationException($"起動済みスロットの A〜D 文字が緑発光色ではありません ({info.ReadySectorKeyColor})。");
                }

                if (info.ReadySectorKeyColor == info.MissingSectorKeyColor)
                {
                    throw new InvalidOperationException("起動済みスロットと停止中スロットの A〜D 文字色が同じで区別できません。");
                }

                if (!(info.LaunchingSectorKeyColor.R > 200 && info.LaunchingSectorKeyColor.G > 170 && info.LaunchingSectorKeyColor.B < 120))
                {
                    throw new InvalidOperationException($"起動中スロットの A〜D 文字が黄色系ではありません ({info.LaunchingSectorKeyColor})。");
                }

                // ライブ情報ピル: 停止中=ローズ赤系、起動=緑系
                if (!(info.MissingInfoPillStatusColor.R > info.MissingInfoPillStatusColor.G + 20
                    && info.MissingInfoPillStatusColor.R > info.MissingInfoPillStatusColor.B + 20))
                {
                    throw new InvalidOperationException($"ライブ情報ピルの停止中ステータスがローズ赤系ではありません ({info.MissingInfoPillStatusColor})。");
                }

                if (!(info.ReadyInfoPillStatusColor.G > info.ReadyInfoPillStatusColor.R + 40))
                {
                    throw new InvalidOperationException($"ライブ情報ピルの起動ステータスが緑系ではありません ({info.ReadyInfoPillStatusColor})。");
                }

                // 1 面フォーカス中の太枠（2.0px）とマウスホバー時の細枠（0.9px）を明確に区別すること
                if (!(info.FocusedSectorStrokeThickness >= 1.8
                    && info.HoveredSectorStrokeThickness <= 1.0
                    && info.FocusedSectorStrokeThickness >= info.HoveredSectorStrokeThickness + 0.8))
                {
                    throw new InvalidOperationException(
                        $"フォーカス枠とホバー枠の太さ差が不足しています (focused={info.FocusedSectorStrokeThickness:0.00}, hovered={info.HoveredSectorStrokeThickness:0.00})。");
                }

                // 右クリックメニュー構成: 先頭が「閉じる／起動する」、縮小と同等のスロット項目＋透過・中央戻しを持ち、
                // 削除対象の「控え一覧」「標準表示」「縮小表示」を含まないこと
                if (info.ReadySlotMenuHeaders.FirstOrDefault() != "閉じる"
                    || info.MissingSlotMenuHeaders.FirstOrDefault() != "起動する")
                {
                    throw new InvalidOperationException(
                        $"右クリックメニュー先頭の起動／閉じる表示が不正です (ready='{info.ReadySlotMenuHeaders.FirstOrDefault()}', missing='{info.MissingSlotMenuHeaders.FirstOrDefault()}')。");
                }

                string[] requiredHeaders = ["IDE", "CLI", "エクスプローラーで開く", "GitHub を開く", "次のディスプレイへ移動", "控えへ移動", "登録内容をクリア...", "普段は透過する（ゴースト表示）", "画面の中央へ戻す"];
                foreach (var req in requiredHeaders)
                {
                    if (!info.ReadySlotMenuHeaders.Contains(req, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException($"右クリックメニューに必須項目 '{req}' がありません。");
                    }
                }

                string[] removedHeaders = ["控え一覧を開く", "控え一覧を閉じる", "標準表示へ戻す", "縮小表示へ切り替え"];
                foreach (var rem in removedHeaders)
                {
                    if (info.ReadySlotMenuHeaders.Contains(rem, StringComparer.Ordinal)
                        || info.MissingSlotMenuHeaders.Contains(rem, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException($"削除対象の右クリックメニュー項目 '{rem}' が残っています。");
                    }
                }

                // 左列 A/C＝文字→ドット、右列 B/D＝ドット→文字の中央寄りミラー配置＆上下対称距離
                if (!info.LeftSectorsPlaceDotOnRight || !info.RightSectorsPlaceDotOnLeft)
                {
                    throw new InvalidOperationException(
                        $"ドットの中央寄りミラー配置が不正です (left={info.LeftSectorsPlaceDotOnRight}, right={info.RightSectorsPlaceDotOnLeft})。");
                }

                if (Math.Abs(info.TopRowLabelOffsetY - info.BottomRowLabelOffsetY) > 1.0)
                {
                    throw new InvalidOperationException(
                        $"A/B と C/D の縦方向中心距離が対称ではありません (top={info.TopRowLabelOffsetY:0.0}, bottom={info.BottomRowLabelOffsetY:0.0})。");
                }

                // AI 作業中のみ中央「非」リング側の内側アークが表示されること
                if (!info.InnerWorkingArcVisibleOnlyWhenWorking)
                {
                    throw new InvalidOperationException("AI 作業中の内側アーク（ホタル点滅）表示制御が期待どおりではありません。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialMissingSlotClickDoesNotLaunch(List<string> failures)
    {
        RunOnStaThread(failures, "未起動スロットの左クリック・中クリックで誤起動しないことの検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var activated = new List<string>();
            var dial = new MicroDialWindow(
                slots,
                ghostOpacityEnabled: true,
                getAuxApplications: () => [],
                onSlotActivated: slot => activated.Add(slot.Name),
                onStoredPanelDropped: (_, _) => { },
                onSlotSwapped: (_, _) => { },
                onToggleVisibility: () => { },
                onToggleStoredOverview: () => { },
                onPinTop: () => { },
                onSendBack: () => { },
                onLaunchAuxApp: _ => { },
                onSwitchStandard: () => { },
                onSwitchCompact: () => { },
                onMinimize: () => { },
                onRequestClose: () => { },
                onGhostOpacityChanged: (_, _) => { },
                onRecenterRequested: () => { },
                onCoreCenterMoved: _ => { });
            try
            {
                // A(index=0)・C(index=2) は Missing のため左クリック・中クリックとも反応しないこと
                dial.SimulateSectorClickForTesting(0, middleClick: false);
                dial.SimulateSectorClickForTesting(2, middleClick: true);
                if (activated.Count != 0)
                {
                    throw new InvalidOperationException($"未起動スロットのクリックで活性化コールバックが呼ばれました ({string.Join(",", activated)})。");
                }

                // B(index=1) は Ready のためクリックでフォーカス切替へ進むこと
                dial.SimulateSectorClickForTesting(1, middleClick: false);
                if (activated.Count != 1 || activated[0] != "B")
                {
                    throw new InvalidOperationException($"起動済みスロット B のクリックが動作しませんでした ({string.Join(",", activated)})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMicroDialAuxMenuButtonTogglesOpenAndClose(List<string> failures)
    {
        RunOnStaThread(failures, "補助 AI アプリボタンのトグル開閉（再押下で閉じる）検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var result = dial.VerifyAuxMenuToggleForTesting();
                if (!result.UsesStaysOpenPopup || !result.OpenedFirst || !result.ClosedSecond || !result.ReopenedThird)
                {
                    throw new InvalidOperationException(
                        $"補助 AI Popup のトグル開閉が期待どおりではありません (staysOpen={result.UsesStaysOpenPopup}, open1={result.OpenedFirst}, close2={result.ClosedSecond}, open3={result.ReopenedThird})。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestStandardTitleBarHasMicroJumpButtonNextToCompactButton(List<string> failures)
    {
        var xamlPath = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel", "MainWindow.xaml"));
        if (!System.IO.File.Exists(xamlPath))
        {
            failures.Add($"MainWindow.xaml が見つかりませんでした ({xamlPath})。");
            return;
        }

        var xaml = System.IO.File.ReadAllText(xamlPath);
        var displayIdx = xaml.IndexOf("x:Name=\"DisplayModeButton\"", StringComparison.Ordinal);
        var microIdx = xaml.IndexOf("x:Name=\"MicroJumpButton\"", StringComparison.Ordinal);
        var minIdx = xaml.IndexOf("x:Name=\"MinimizeButton\"", StringComparison.Ordinal);
        if (displayIdx < 0 || microIdx < 0 || minIdx < 0 || !(displayIdx < microIdx && microIdx < minIdx))
        {
            failures.Add("標準タイトルバーで MicroJumpButton が DisplayModeButton の右隣（MinimizeButton の左）に配置されていません。");
        }
    }

    private static void TestMicroDialLeftArcHasDownloadsAndPullRequestButtons(List<string> failures)
    {
        RunOnStaThread(failures, "左弧のダウンロード＆PR受信箱ボタン配置と未読赤丸バッジ検証", () =>
        {
            var slots = CreateMixedStatusSlots();
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                var info = dial.VerifyLeftToolSatellitesForTesting();
                if (!(info.DownloadsCenter.X < MicroDialWindow.StageCenterDip - MicroDialWindow.CoreRadiusDip
                    && info.PullRequestCenter.X < MicroDialWindow.StageCenterDip - MicroDialWindow.CoreRadiusDip))
                {
                    throw new InvalidOperationException(
                        $"ダウンロード・PR ボタンが中央円より左側に配置されていません (dlX={info.DownloadsCenter.X:0.0}, prX={info.PullRequestCenter.X:0.0})。");
                }

                if (!(info.DownloadsCenter.Y < info.PullRequestCenter.Y))
                {
                    throw new InvalidOperationException(
                        $"左弧ボタンの上下順（上＝ダウンロード、下＝PR）が期待と異なります (dlY={info.DownloadsCenter.Y:0.0}, prY={info.PullRequestCenter.Y:0.0})。");
                }

                if (!(info.DownloadsClearanceFromCore is >= 8.0 and <= 10.5
                    && info.PullRequestClearanceFromCore is >= 8.0 and <= 10.5))
                {
                    throw new InvalidOperationException(
                        $"左弧ボタンと中央円の隙間が他サテライト（9.0 DIP）と一致しません (dl={info.DownloadsClearanceFromCore:0.00}, pr={info.PullRequestClearanceFromCore:0.00})。");
                }

                if (!info.BadgeVisibleWhenActive || !info.BadgeCollapsedWhenInactive)
                {
                    throw new InvalidOperationException("PR 未読赤丸バッジの表示切替が期待どおりではありません。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestFocusNameOverlayPlacedBelowMicroDial(List<string> failures)
    {
        var work = new WindowArranger.WindowBounds(0, 0, 1920, 1040);
        const int overlayW = 460;
        const int overlayH = 190;

        // 通常時（極小表示なし）は作業領域中央
        var (_, normalTop) = FocusNameOverlay.ComputeOverlayPosition(work, overlayW, overlayH, avoidRectScreenPx: null, dpiScaleY: 1.0);
        if (Math.Abs(normalTop - ((1040 - overlayH) / 2)) > 1)
        {
            failures.Add($"通常時の FocusNameOverlay が中央配置ではありません (top={normalTop})。");
        }

        // 極小表示（画面中央に展開 190x190）があるときは、極小表示の下側へ重ならずに配置されること
        var dialCenterAvoid = new Rect(960 - 95, 520 - 95, 190, 190);
        var (_, microBelowTop) = FocusNameOverlay.ComputeOverlayPosition(work, overlayW, overlayH, dialCenterAvoid, dpiScaleY: 1.0);
        if (microBelowTop < dialCenterAvoid.Bottom + 12)
        {
            failures.Add($"極小表示時の FocusNameOverlay が極小表示の下側へ避けられていません (top={microBelowTop}, dialBottom={dialCenterAvoid.Bottom})。");
        }

        // 別ディスプレイのフォーカス時は対象モニタと重ならないため通常どおり中央
        var otherMonitorWork = new WindowArranger.WindowBounds(1920, 0, 1920, 1040);
        var (_, otherTop) = FocusNameOverlay.ComputeOverlayPosition(otherMonitorWork, overlayW, overlayH, dialCenterAvoid, dpiScaleY: 1.0);
        if (Math.Abs(otherTop - ((1040 - overlayH) / 2)) > 1)
        {
            failures.Add($"別ディスプレイの FocusNameOverlay が中央配置になっていません (top={otherTop})。");
        }

        // 極小ダイヤルが画面下端に寄って下に空きが無いときは上側へ自動退避すること
        var dialBottomEdgeAvoid = new Rect(960 - 95, 840, 190, 190);
        var (_, fallbackAboveTop) = FocusNameOverlay.ComputeOverlayPosition(work, overlayW, overlayH, dialBottomEdgeAvoid, dpiScaleY: 1.0);
        if (fallbackAboveTop + overlayH > dialBottomEdgeAvoid.Top - 12)
        {
            failures.Add($"下端配置時の FocusNameOverlay 上側退避が機能していません (top={fallbackAboveTop}, dialTop={dialBottomEdgeAvoid.Top})。");
        }
    }

    private static List<WindowSlot> CreateMixedStatusSlots()
    {
        var a = MakeSlot("A", "AI 利用量チェック", "vscode");
        a.WindowStatus = SlotWindowStatus.Missing;
        var b = MakeSlot("B", "タートルHub", "vscode");
        b.WindowStatus = SlotWindowStatus.Ready;
        var c = MakeSlot("C", "AI スタディ", "vscode");
        c.WindowStatus = SlotWindowStatus.Missing;
        var d = MakeSlot("D", "Gemini-Skills", "antigravity");
        d.WindowStatus = SlotWindowStatus.Launching;
        return [a, b, c, d];
    }

    private static void TestMicroDialTaskbarThumbnailIsOpaqueWithoutGhostOpacity(List<string> failures)
    {
        RunOnStaThread(failures, "タスクバーのサムネイル画像が新しい円形ダイヤルを非透過（ゴースト透過なし・全面不透明）で描くことの検証", () =>
        {
            // AI 作業中・完了の点滅アニメーションが入ると撮影ごとに画素が揺れるため、待機中のみの 4 スロットで比較する。
            var slots = new List<WindowSlot>
            {
                MakeSlot("A", "AI-Usage-Checker"),
                MakeSlot("B", "タートルHub"),
                MakeSlot("C", "AI-Agent-Lab", "antigravity"),
                MakeSlot("D", "Gemini-Skills", "codex-cli")
            };
            var dial = CreateDial(slots, ghostOpacityEnabled: true);
            try
            {
                // 非ホバー時のゴースト透過（既定 30%）が本番ダイヤルに掛かっている状態で撮る。
                var ghostedStage = dial.RenderStageOffscreenForPreview(expanded: false, previewHoveredSector: null, ghostWhenCollapsed: true);
                var ghostedThumbnail = dial.RenderTaskbarThumbnailImage(200, 112);
                var solidStage = dial.RenderStageOffscreenForPreview(expanded: false, previewHoveredSector: null, ghostWhenCollapsed: false);
                var solidThumbnail = dial.RenderTaskbarThumbnailImage(200, 112);

                static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource bmp)
                {
                    var stride = bmp.PixelWidth * 4;
                    var buffer = new byte[stride * bmp.PixelHeight];
                    bmp.CopyPixels(buffer, stride, 0);
                    return buffer;
                }

                // 前提: 本番ダイヤル（ステージ）側は本当にゴースト透過で薄くなっていること（円中心の α が明確に低い）。
                var ghostedStagePixels = Pixels(ghostedStage);
                var solidStagePixels = Pixels(solidStage);
                var stageCenterAlphaIndex = ((ghostedStage.PixelHeight / 2) * ghostedStage.PixelWidth * 4) + ((ghostedStage.PixelWidth / 2) * 4) + 3;
                if (ghostedStagePixels[stageCenterAlphaIndex] >= 200 || solidStagePixels[stageCenterAlphaIndex] != 255)
                {
                    throw new InvalidOperationException(
                        $"検証の前提となるゴースト透過状態を作れませんでした (ghostedAlpha={ghostedStagePixels[stageCenterAlphaIndex]}, solidAlpha={solidStagePixels[stageCenterAlphaIndex]})。");
                }

                // 最大サイズ（200x112）に収まる正方形で、凍結済みであること。
                if (ghostedThumbnail.PixelWidth != 112 || ghostedThumbnail.PixelHeight != 112 || !ghostedThumbnail.IsFrozen)
                {
                    throw new InvalidOperationException(
                        $"サムネイル画像が最大サイズに収まる正方形になっていません ({ghostedThumbnail.PixelWidth}x{ghostedThumbnail.PixelHeight}, frozen={ghostedThumbnail.IsFrozen})。");
                }

                var pixels = Pixels(ghostedThumbnail);
                var w = ghostedThumbnail.PixelWidth;
                var h = ghostedThumbnail.PixelHeight;

                // 全ピクセル α=255（円の外側も含めて背後が一切透けない）。
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 255)
                    {
                        throw new InvalidOperationException($"サムネイル画像に透過ピクセルが含まれています (index={i / 4}, alpha={pixels[i]})。");
                    }
                }

                // 四隅は本体パネルと同じ不透明背景 #101211（B,G,R = 0x11,0x12,0x10）。
                foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
                {
                    var p = ((y * w) + x) * 4;
                    if (pixels[p] != 0x11 || pixels[p + 1] != 0x12 || pixels[p + 2] != 0x10)
                    {
                        throw new InvalidOperationException(
                            $"サムネイル画像の四隅が本体パネルの背景色になっていません ((x,y)=({x},{y}), BGR=({pixels[p]:X2},{pixels[p + 1]:X2},{pixels[p + 2]:X2}))。");
                    }
                }

                // 円形ダイヤルが実際に描かれていること（中心付近の中央ボタン「非」の領域が背景色ではない）。
                var nonBackground = 0;
                for (var p = 0; p < pixels.Length; p += 4)
                {
                    if (pixels[p] != 0x11 || pixels[p + 1] != 0x12 || pixels[p + 2] != 0x10)
                    {
                        nonBackground++;
                    }
                }

                if (nonBackground < (w * h) / 4)
                {
                    throw new InvalidOperationException($"サムネイル画像に円形ダイヤルが描かれていません (nonBackground={nonBackground}/{w * h})。");
                }

                // ゴースト透過の有無で 1 画素も変わらない（非ホバー時の薄い表示ではなく常に不透明で映る）。
                if (!pixels.AsSpan().SequenceEqual(Pixels(solidThumbnail)))
                {
                    throw new InvalidOperationException("サムネイル画像が本番ダイヤルのゴースト透過の影響を受けて薄くなっています。");
                }
            }
            finally
            {
                dial.Close();
            }
        });
    }

    private static void TestMainWindowTaskbarThumbnailShowsDialOnlyWhileMicroDialActive(List<string> failures)
    {
        var panelDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TurtleAIQuartetHub.Panel"));
        var thumbnailCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.TaskbarThumbnail.cs"));
        var mainCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.xaml.cs"));
        var microCs = System.IO.File.ReadAllText(System.IO.Path.Combine(panelDir, "MainWindow.MicroDial.cs"));

        // iconic サムネイルは「円形ダイヤルが本番表示されている間」だけ有効にし、それ以外は既定のライブサムネイルへ戻す。
        if (!thumbnailCs.Contains("var shouldUseIconic = IsMicroDialActive;", StringComparison.Ordinal)
            || !thumbnailCs.Contains("DwmwaForceIconicRepresentation", StringComparison.Ordinal)
            || !thumbnailCs.Contains("DwmwaHasIconicBitmap", StringComparison.Ordinal)
            || !thumbnailCs.Contains("dial.RenderTaskbarThumbnailImage(maxWidth, maxHeight)", StringComparison.Ordinal)
            || !thumbnailCs.Contains("DwmSetIconicThumbnail(hwnd, hBitmap, 0)", StringComparison.Ordinal))
        {
            failures.Add("タスクバーのサムネイルが極小ダイヤル稼働中だけ円形ダイヤル画像（iconic）へ切り替わる構成になっていません。");
        }

        // サムネイル差し替えは Cloak や本番ダイヤルの透過度など既存の表示処理に触れない。
        if (thumbnailCs.Contains("SetCloaked(", StringComparison.Ordinal)
            || thumbnailCs.Contains("BeginAnimation(", StringComparison.Ordinal)
            || thumbnailCs.Contains("SetGhostOpacity", StringComparison.Ordinal))
        {
            failures.Add("タスクバーのサムネイル差し替え処理が Cloak・アニメーション・透過度など既存の表示処理へ干渉しています。");
        }

        if (!mainCs.Contains("case WmDwmSendIconicThumbnail:", StringComparison.Ordinal)
            || !mainCs.Contains("TryProvideTaskbarIconicThumbnail(hwnd, lParam)", StringComparison.Ordinal))
        {
            failures.Add("MainWindow のウィンドウプロシージャが WM_DWMSENDICONICTHUMBNAIL に応答していません。");
        }

        // ダイヤル破棄・一時退避（本体露出）・状態同期の各経路でサムネイル方式を追従させる。
        var disposeStart = microCs.IndexOf("private void DisposeMicroDialWindow()", StringComparison.Ordinal);
        var suspendStart = microCs.IndexOf("private void SuspendMicroDialForMainOverlay(", StringComparison.Ordinal);
        var resumeStart = microCs.IndexOf("private void ResumeMicroDialAfterMainOverlay(", StringComparison.Ordinal);
        var syncStart = microCs.IndexOf("private void SyncMicroDialUiState()", StringComparison.Ordinal);
        if (disposeStart < 0 || suspendStart <= disposeStart || resumeStart <= suspendStart || syncStart < 0)
        {
            failures.Add("MainWindow.MicroDial.cs にダイヤル破棄・一時退避・状態同期のメソッドが見つかりません。");
            return;
        }

        var disposeBody = microCs[disposeStart..suspendStart];
        var suspendBody = microCs[suspendStart..resumeStart];
        var syncBody = microCs[syncStart..Math.Min(microCs.Length, syncStart + 400)];
        var suspendSyncIndex = suspendBody.IndexOf("SyncTaskbarThumbnailRepresentation();", StringComparison.Ordinal);
        var suspendUncloakIndex = suspendBody.IndexOf("SetCloaked(new WindowInteropHelper(this).Handle, false);", StringComparison.Ordinal);
        if (!disposeBody.Contains("SyncTaskbarThumbnailRepresentation();", StringComparison.Ordinal)
            || suspendSyncIndex < 0
            || suspendUncloakIndex < 0
            || suspendSyncIndex > suspendUncloakIndex
            || !syncBody.Contains("SyncTaskbarThumbnailRepresentation();", StringComparison.Ordinal))
        {
            failures.Add("ダイヤル破棄・一時退避（本体露出前）・状態同期の各経路でタスクバーのサムネイル方式が追従していません。");
        }
    }

    private static List<WindowSlot> CreateSampleSlots()
    {
        var a = MakeSlot("A", "AI-Usage-Checker", "vscode");
        a.IsFocused = true;
        var b = MakeSlot("B", "タートルHub", "vscode");
        b.AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"c:\work", "s1"), DateTimeOffset.UtcNow);
        b.NotifyAiActivityChanged();
        var c = MakeSlot("C", "AI-Agent-Lab", "antigravity");
        var d = MakeSlot("D", "Gemini-Skills", "codex-cli");
        d.AiTracker.Apply(new AiHookEvent("codex", "UserPromptSubmit", AiHookEventKind.Start, @"c:\work", "s2"), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30));
        d.AiTracker.Apply(new AiHookEvent("codex", "Stop", AiHookEventKind.Stop, @"c:\work", "s2"), DateTimeOffset.UtcNow);
        d.NotifyAiActivityChanged();
        return [a, b, c, d];
    }

    private static WindowSlot MakeSlot(string name, string title, string appId = "vscode")
    {
        return new WindowSlot(new SlotConfig { Name = name, ApplicationId = appId })
        {
            PanelTitle = title,
            ApplicationShortName = appId == "antigravity" ? "Antigravity" : appId == "codex-cli" ? "Codex" : "VS Code",
            ApplicationDisplayName = appId == "antigravity" ? "Antigravity" : appId == "codex-cli" ? "Codex" : "VS Code",
            WindowStatus = SlotWindowStatus.Ready
        };
    }

    private static MicroDialWindow CreateDial(IReadOnlyList<WindowSlot> slots, bool ghostOpacityEnabled)
    {
        return new MicroDialWindow(
            slots,
            ghostOpacityEnabled,
            getAuxApplications: () => [],
            onSlotActivated: _ => { },
            onStoredPanelDropped: (_, _) => { },
            onSlotSwapped: (_, _) => { },
            onToggleVisibility: () => { },
            onToggleStoredOverview: () => { },
            onPinTop: () => { },
            onSendBack: () => { },
            onLaunchAuxApp: _ => { },
            onSwitchStandard: () => { },
            onSwitchCompact: () => { },
            onMinimize: () => { },
            onRequestClose: () => { },
            onGhostOpacityChanged: (_, _) => { },
            onRecenterRequested: () => { },
            onCoreCenterMoved: _ => { });
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
