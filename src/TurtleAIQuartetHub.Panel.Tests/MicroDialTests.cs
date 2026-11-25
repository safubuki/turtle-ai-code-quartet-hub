using System.Text.Json;
using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class MicroDialTests
{
    public const int Count = 15;

    public static void Run(List<string> failures)
    {
        TestSmartLabelsSkipSharedTokenPrefix(failures);
        TestSmartLabelsSkipSharedJapanesePrefix(failures);
        TestSmartLabelsDefaultTitleReturnsEmpty(failures);
        TestGhostOpacityConfigDefaultAndBackwardCompat(failures);
        TestMicroDialOffscreenStructureOpacityAndWheel(failures);
        TestMicroDialCaptureCoreCircleImage(failures);
        TestMicroDialStoredAssignAndHiddenStateTransitions(failures);
        TestMicroDialModeButtonIconUsesCircularDialVector(failures);
        TestMicroDialBottomArcOrderAndStoredPillClearance(failures);
        TestMicroDialDarkContextMenuAndStatusVisualContrast(failures);
        TestMicroDialMissingSlotClickDoesNotLaunch(failures);
        TestMicroDialAuxMenuButtonTogglesOpenAndClose(failures);
        TestStandardTitleBarHasMicroJumpButtonNextToCompactButton(failures);
        TestMicroDialLeftArcHasDownloadsAndPullRequestButtons(failures);
        TestFocusNameOverlayPlacedBelowMicroDial(failures);
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
        if (!fresh.MicroGhostOpacityEnabled)
        {
            failures.Add("AppConfig.MicroGhostOpacityEnabled の既定値が true ではありません。");
        }

        // 旧設定ファイル（MicroGhostOpacityEnabled キー無し）を読み込んでも既定値 true を維持すること。
        var legacy = JsonSerializer.Deserialize<AppConfig>("""{"CodeCommand":"code","Gap":6}""");
        if (legacy is null || !legacy.MicroGhostOpacityEnabled)
        {
            failures.Add("旧設定 JSON からの復元時に MicroGhostOpacityEnabled が true になりませんでした。");
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

                if (!(result.IdleGhostOpacityValue is >= 0.35 and <= 0.65))
                {
                    throw new InvalidOperationException($"非ホバー透過率が想定範囲外です ({result.IdleGhostOpacityValue:0.00})。");
                }

                if (!(result.IdleAiAlertOpacityValue > result.IdleGhostOpacityValue + 0.2))
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
                onGhostOpacityChanged: _ => { },
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
            onGhostOpacityChanged: _ => { },
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
