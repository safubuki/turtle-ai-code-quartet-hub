using System.IO;
using System.Xml.Linq;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class SystemMonitorAndCardTitleTests
{
    public const int Count = 23;

    public static void Run(List<string> failures)
    {
        VerifyFormatBytes(failures);
        VerifyDirectorySizeCalculation(failures);
        VerifyRelativeFolderRatios(failures);
        VerifySystemMonitorSnapshotAndDrives(failures);
        VerifyBuildFolderUsageItems(failures);
        VerifyMainWindowXamlCardTitleAndSystemMonitorLayout(failures);
    }

    private static void VerifyFormatBytes(List<string> failures)
    {
        Check(failures, "FormatBytes(0)", SystemMonitorService.FormatBytes(0) == "0 B", SystemMonitorService.FormatBytes(0));
        Check(failures, "FormatBytes(512)", SystemMonitorService.FormatBytes(512) == "512 B", SystemMonitorService.FormatBytes(512));
        Check(failures, "FormatBytes(2048)", SystemMonitorService.FormatBytes(2048) == "2 KB", SystemMonitorService.FormatBytes(2048));
        Check(failures, "FormatBytes(5 MB)", SystemMonitorService.FormatBytes(5L * 1024 * 1024) == "5.0 MB", SystemMonitorService.FormatBytes(5L * 1024 * 1024));
        Check(failures, "FormatBytes(2.5 GB)", SystemMonitorService.FormatBytes((long)(2.5 * 1024 * 1024 * 1024)) == "2.5 GB", SystemMonitorService.FormatBytes((long)(2.5 * 1024 * 1024 * 1024)));
    }

    private static void VerifyDirectorySizeCalculation(List<string> failures)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TurtleAIQuartetHub_MonitorTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var subDir = Path.Combine(tempRoot, "sub");
            Directory.CreateDirectory(subDir);

            File.WriteAllBytes(Path.Combine(tempRoot, "a.bin"), new byte[1200]);
            File.WriteAllBytes(Path.Combine(subDir, "b.bin"), new byte[2800]);

            var (totalBytes, fileCount) = SystemMonitorService.CalculateDirectorySize(tempRoot, CancellationToken.None);
            Check(
                failures,
                "CalculateDirectorySize 合計バイト数とファイル数が一致すること",
                totalBytes == 4000 && fileCount == 2,
                $"totalBytes={totalBytes}, fileCount={fileCount}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // 一時ディレクトリ削除失敗は無視
            }
        }
    }

    private static void VerifyRelativeFolderRatios(List<string> failures)
    {
        var item1 = new FolderUsageItem("A", "Slot", "Slot A", @"C:\a", @"C:\a", "計測中...", false);
        var item2 = new FolderUsageItem("B", "Slot", "Slot B", @"C:\b", @"C:\b", "計測中...", false);
        item1.ApplyMeasuredSize(200L * 1024 * 1024, 10, "200.0 MB");
        item2.ApplyMeasuredSize(50L * 1024 * 1024, 5, "50.0 MB");

        var list = new List<FolderUsageItem> { item1, item2 };
        SystemMonitorService.UpdateRelativeFolderRatios(list);

        Check(
            failures,
            "UpdateRelativeFolderRatios 最大フォルダが100%で比率計算されること",
            Math.Abs(item1.UsageRatio - 100d) < 0.01 && Math.Abs(item2.UsageRatio - 25d) < 0.01,
            $"item1={item1.UsageRatio}, item2={item2.UsageRatio}");
    }

    private static void VerifySystemMonitorSnapshotAndDrives(List<string> failures)
    {
        var service = new SystemMonitorService();
        var snapshot = service.CaptureSystemResources(activeManagedWindowCount: 2);

        Check(
            failures,
            "CaptureSystemResources 物理メモリ合計が正の値で取得できること",
            snapshot.TotalMemoryBytes > 0 && snapshot.MemoryUsagePercent >= 0d && snapshot.MemoryUsagePercent <= 100d,
            $"Total={snapshot.TotalMemoryBytes}, Percent={snapshot.MemoryUsagePercent}");

        Check(
            failures,
            "CaptureSystemResources CPU詳細テキストに管理中ウィンドウ数が含まれること",
            snapshot.CpuDetailText.Contains("2 個", StringComparison.Ordinal) && snapshot.CpuUsagePercent >= 0d && snapshot.CpuUsagePercent <= 100d,
            snapshot.CpuDetailText);

        var drives = service.GetDriveUsages();
        Check(
            failures,
            "GetDriveUsages 少なくとも1つの準備完了ドライブを取得できること",
            drives.Count >= 1 && drives.All(d => d.TotalBytes > 0 && d.UsagePercent >= 0d && d.UsagePercent <= 100d),
            $"drives.Count={drives.Count}");
    }

    private static void VerifyBuildFolderUsageItems(List<string> failures)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TurtleAIQuartetHub_FolderItems_{Guid.NewGuid():N}");
        try
        {
            var slotADir = Path.Combine(tempRoot, "workspaceA");
            var storedDir = Path.Combine(tempRoot, "stored1");
            var stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(slotADir);
            Directory.CreateDirectory(storedDir);
            Directory.CreateDirectory(stateDir);

            var slotA = new WindowSlot(new SlotConfig { Name = "A", Path = slotADir }) { PanelTitle = "メインA" };
            var slotB = new WindowSlot(new SlotConfig { Name = "B" }) { PanelTitle = "メインB" };
            var stored1 = new StoredPanelSlot(1)
            {
                PanelTitle = "保存案件1",
                WorkspacePath = storedDir
            };

            var config = new AppConfig
            {
                StateDirectory = stateDir
            };

            var service = new SystemMonitorService();
            var items = service.BuildFolderUsageItems([slotA, slotB], [stored1], config);

            Check(
                failures,
                "BuildFolderUsageItems スロット・控え・Hubデータフォルダが抽出されること",
                items.Any(x => x.BadgeText == "A" && x.CanOpenFolder)
                && items.Any(x => x.BadgeText == "B" && !x.CanOpenFolder && x.SizeText == "未設定")
                && items.Any(x => x.CategoryKind == "Stored" && x.CanOpenFolder)
                && items.Any(x => x.BadgeText == "Hub" && x.CanOpenFolder),
                $"items={string.Join(", ", items.Select(i => $"{i.BadgeText}:{i.SizeText}"))}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // 無視
            }
        }
    }

    private static void VerifyMainWindowXamlCardTitleAndSystemMonitorLayout(List<string> failures)
    {
        var xamlPath = FindMainWindowXamlPath();
        Check(failures, "MainWindow.xaml が検出できること", !string.IsNullOrWhiteSpace(xamlPath) && File.Exists(xamlPath), xamlPath ?? "null");
        if (string.IsNullOrWhiteSpace(xamlPath) || !File.Exists(xamlPath))
        {
            return;
        }

        var doc = XDocument.Load(xamlPath);
        var root = doc.Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 1. ウィンドウ全体サイズが案C＋1行フッターでスリム化された 616x392 になっていること
        var widthAttr = (string?)root.Attribute("Width");
        var heightAttr = (string?)root.Attribute("Height");
        Check(
            failures,
            "MainWindow の基準サイズ(616x392)が維持されていること",
            widthAttr == "616" && heightAttr == "392",
            $"Width={widthAttr}, Height={heightAttr}");

        // 1b. 標準表示カードが案C（IDE 2種常設＋CLIポップオーバー）構成で、全ディスプレイ移動が控え一覧の左隣にあり、
        //     フッターが1行5ボタン（最前面→最背面→非表示→すべて閉じる→大きな一括起動、メッセージ欄なし）であること
        var cliPickerPopup = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "SlotCliPickerPopup");
        var aiAppsPopup = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "StandardAiAppsPopup");
        var storedRowGrid = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "StoredPanelsRowGrid");
        var monitorInStoredRow = storedRowGrid?.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "ToggleMonitorButton");
        var aiAppsButtonInStoredRow = storedRowGrid?.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "StandardAiAppsPickerButton");
        var footerGrid = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "FooterControlsGrid");
        var footerButtonNames = footerGrid?.Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Select(e => (string?)e.Attribute(x + "Name") ?? string.Empty)
            .ToList() ?? [];
        var footerExpectedOrder = new[]
        {
            "TopmostAllButton",
            "BackmostAllButton",
            "ToggleVisibilityButton",
            "CloseAllButton",
            "LaunchButton"
        };
        var hasMessageBoxInFooter = footerGrid?.Descendants()
            .Any(e => ((string?)e.Attribute("Text"))?.Contains("Message", StringComparison.Ordinal) == true) == true;
        Check(
            failures,
            "標準表示が案C（CLI・AIアプリ各ポップオーバーは StaysOpen=True トグル）＋全ディスプレイ移動の上段配置＋アイコン付き1行5ボタンフッターになっていること",
            cliPickerPopup is not null
            && (string?)cliPickerPopup.Attribute("StaysOpen") == "True"
            && aiAppsPopup is not null
            && (string?)aiAppsPopup.Attribute("StaysOpen") == "True"
            && aiAppsButtonInStoredRow is not null
            && monitorInStoredRow is not null
            && !hasMessageBoxInFooter
            && footerButtonNames.SequenceEqual(footerExpectedOrder),
            $"cliPopup={cliPickerPopup is not null}(staysOpen={(string?)cliPickerPopup?.Attribute("StaysOpen")}), aiAppsPopup={aiAppsPopup is not null}(staysOpen={(string?)aiAppsPopup?.Attribute("StaysOpen")}), aiBtn={aiAppsButtonInStoredRow is not null}, monitorInStoredRow={monitorInStoredRow is not null}, hasMsg={hasMessageBoxInFooter}, buttons=[{string.Join(", ", footerButtonNames)}]");

        // 2. SlotCardTemplate のタイトル TextBox が FontWeight=Bold かつ拡大フォント(16.5)になっていること、
        //    さらに中段・下段ボタンが単一の 3 列×2 行グリッド（SlotCardActionsGrid）で揃っていること
        var slotCardTemplate = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(x + "Key") == "SlotCardTemplate");
        Check(failures, "SlotCardTemplate が存在すること", slotCardTemplate is not null, "SlotCardTemplate");

        if (slotCardTemplate is not null)
        {
            var titleTextBox = slotCardTemplate.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TextBox"
                    && ((string?)e.Attribute("Text"))?.Contains("PanelTitle", StringComparison.Ordinal) == true);
            Check(
                failures,
                "SlotCardTemplate のタイトル TextBox が Bold かつ FontSize=16.5 で InlineTitleTextBox スタイルを維持していること",
                titleTextBox is not null
                && (string?)titleTextBox.Attribute("FontWeight") == "Bold"
                && (string?)titleTextBox.Attribute("FontSize") == "16.5"
                && ((string?)titleTextBox.Attribute("Style"))?.Contains("InlineTitleTextBox", StringComparison.Ordinal) == true,
                titleTextBox?.ToString() ?? "null");

            var actionsGrid = slotCardTemplate.Descendants()
                .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "SlotCardActionsGrid");
            var rowHeights = actionsGrid?.Element(actionsGrid.Name.Namespace + "Grid.RowDefinitions")?
                .Elements().Select(r => (string?)r.Attribute("Height")).ToList() ?? [];
            var colWidths = actionsGrid?.Element(actionsGrid.Name.Namespace + "Grid.ColumnDefinitions")?
                .Elements().Select(c => (string?)c.Attribute("Width")).ToList() ?? [];
            var storeButton = actionsGrid?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "StoreSlotButton_Click");
            Check(
                failures,
                "SlotCardActionsGrid が 3 列×2 行（高さ 26・隙間 6・CLI/控え移動/閉じる同幅 62・右寄せ）で揃っていること",
                actionsGrid is not null
                && rowHeights.SequenceEqual(["26", "6", "26"])
                && colWidths.SequenceEqual(["*", "6", "*", "6", "62"])
                && (string?)storeButton?.Attribute("Width") == "62"
                && (string?)storeButton?.Attribute("HorizontalAlignment") == "Right",
                $"rows=[{string.Join(",", rowHeights)}], cols=[{string.Join(",", colWidths)}], storeW={(string?)storeButton?.Attribute("Width")}, storeAlign={(string?)storeButton?.Attribute("HorizontalAlignment")}");
        }

        // 2b. 控え Quartet（タブと 4 枚の控えカード）は標準・縮小とも置かず、控えは一覧から選んで
        //     スロット側の「ここに表示」で配置すること。一覧には A〜D の配置先パッドを置かないこと。
        var removedStoredQuartetKeys = new[] { "CompactStoredPanelTemplate", "StoredPanelTemplate", "StoredPanelPageTemplate", "CompactStoredPageTemplate" };
        var removedStoredQuartetNames = new[] { "StoredPanelsExpander", "CompactStoredPanelsArea", "StoredOverviewTargetPad", "CompactStoredPanelsToggleButton" };
        var leftoverStoredQuartet = root.Descendants()
            .Where(e => removedStoredQuartetKeys.Contains((string?)e.Attribute(x + "Key"))
                || removedStoredQuartetNames.Contains((string?)e.Attribute(x + "Name")))
            .Select(e => (string?)e.Attribute(x + "Key") ?? (string?)e.Attribute(x + "Name"))
            .ToList();
        Check(
            failures,
            "控え Quartet（タブ・控えカード）と一覧の A〜D パッドが標準・縮小とも無いこと",
            leftoverStoredQuartet.Count == 0,
            string.Join(", ", leftoverStoredQuartet));
        var compactSlotTemplate = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(x + "Key") == "CompactSlotTemplate");
        var standardSlotTemplate = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(x + "Key") == "SlotCardTemplate");
        Check(
            failures,
            "標準表示のスロットカードも控えの選択中に「ここに表示」へ切り替わり、クリックで配置できること",
            standardSlotTemplate is not null
            && standardSlotTemplate.Descendants().Any(e => e.Name.LocalName == "Trigger"
                && (string?)e.Attribute("Property") == "local:SlotAssignTarget.IsArmed")
            && standardSlotTemplate.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "SlotAssignOverlay"
                && (string?)e.Attribute("MouseLeftButtonUp") == "SlotAssignOverlay_MouseLeftButtonUp"),
            "SlotCardTemplate");
        Check(
            failures,
            "縮小表示のスロットが控えの選択中に「ここに表示」へ切り替わるトリガーを持つこと",
            compactSlotTemplate is not null
            && compactSlotTemplate.Descendants().Any(e => e.Name.LocalName == "Trigger"
                && (string?)e.Attribute("Property") == "local:SlotAssignTarget.IsArmed")
            && compactSlotTemplate.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "CompactSlotAssignPill"),
            "CompactSlotTemplate");
        // 控え一覧の開閉ボタンは標準・縮小で共通の 1 つ。AI アプリ（左）と同じ行の右端（Grid.Column=1）に置く。
        var overviewToggleButton = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "StoredOverviewToggleButton");
        var auxiliaryRow = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "AuxiliaryApplicationsRow");
        Check(
            failures,
            "控え一覧の開閉ボタンが AI アプリの右側（右端列）にあり、一覧を開くこと",
            overviewToggleButton is not null
            && (string?)overviewToggleButton.Attribute("Click") == "StoredOverviewButton_Click"
            && (string?)overviewToggleButton.Attribute("Grid.Column") == "2"
            && overviewToggleButton.Parent is not null
            && ReferenceEquals(overviewToggleButton.Parent, auxiliaryRow?.Parent)
            && (string?)auxiliaryRow?.Attribute("HorizontalAlignment") == "Left",
            overviewToggleButton?.ToString() ?? "null");

        // 3. TitleBarButtonsPanel で SystemMonitorButton → DownloadsFolderButton → PullRequestInboxButton → SettingsButton の順に並ぶこと
        var titleBarButtonsPanel = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "TitleBarButtonsPanel");
        Check(failures, "TitleBarButtonsPanel が存在すること", titleBarButtonsPanel is not null, "TitleBarButtonsPanel");

        if (titleBarButtonsPanel is not null)
        {
            var buttons = titleBarButtonsPanel.Descendants()
                .Where(e => e.Name.LocalName == "Button")
                .ToList();
            var buttonNames = buttons
                .Select(e => (string?)e.Attribute(x + "Name") ?? string.Empty)
                .ToList();
            var monitorIdx = buttonNames.IndexOf("SystemMonitorButton");
            var downloadsIdx = buttonNames.IndexOf("DownloadsFolderButton");
            var inboxIdx = buttonNames.IndexOf("PullRequestInboxButton");
            var settingsIdx = buttonNames.IndexOf("SettingsButton");
            var downloadsButton = downloadsIdx >= 0 ? buttons[downloadsIdx] : null;
            Check(
                failures,
                "SystemMonitorButton の右に DownloadsFolderButton、PullRequestInboxButton の左隣に DownloadsFolderButton、その右に SettingsButton が配置されていること",
                monitorIdx >= 0
                && downloadsIdx == monitorIdx + 1
                && inboxIdx == downloadsIdx + 1
                && settingsIdx == inboxIdx + 1
                && (string?)downloadsButton?.Attribute("Click") == "DownloadsFolderButton_Click",
                string.Join(", ", buttonNames));
        }

        // 4. SettingsOverlay の AI 作業通知内に ResetAiActivityButton が配置されていること
        var settingsOverlay = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "SettingsOverlay");
        var resetAiButton = settingsOverlay?.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "ResetAiActivityButton");
        Check(
            failures,
            "設定画面の AI 作業通知に ResetAiActivityButton が配置されクリックハンドラが結線されていること",
            resetAiButton is not null
            && (string?)resetAiButton.Attribute("Click") == "ResetAiActivityButton_Click",
            resetAiButton?.ToString() ?? "null");

        // 5. SystemMonitorOverlay が存在し、メモリ・CPU・ドライブ・フォルダの各表示コントロールを備えていること
        var overlay = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "SystemMonitorOverlay");
        Check(
            failures,
            "SystemMonitorOverlay と内部のメモリ・CPU・ドライブ・フォルダ表示要素が存在すること",
            overlay is not null
            && overlay.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "SystemMonitorMemoryProgressBar")
            && overlay.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "SystemMonitorCpuProgressBar")
            && overlay.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "SystemMonitorDrivesItemsControl")
            && overlay.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "SystemMonitorFoldersItemsControl"),
            "SystemMonitorOverlay structure");
    }

    private static string? FindMainWindowXamlPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TurtleAIQuartetHub.Panel", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void Check(List<string> failures, string name, bool condition, string detail)
    {
        if (!condition)
        {
            failures.Add($"{name}: {detail}");
        }
    }
}
