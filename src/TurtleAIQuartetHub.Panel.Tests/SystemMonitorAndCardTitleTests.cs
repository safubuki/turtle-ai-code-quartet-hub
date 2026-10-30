using System.IO;
using System.Xml.Linq;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class SystemMonitorAndCardTitleTests
{
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

        // 1. ウィンドウ全体サイズが 650x535 のままで圧迫・肥大化していないこと
        var widthAttr = (string?)root.Attribute("Width");
        var heightAttr = (string?)root.Attribute("Height");
        Check(
            failures,
            "MainWindow の基準サイズ(650x535)が維持されていること",
            widthAttr == "650" && heightAttr == "535",
            $"Width={widthAttr}, Height={heightAttr}");

        // 2. SlotCardTemplate のタイトル TextBox が FontWeight=Bold かつ拡大フォント(15.5)になっていること
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
                "SlotCardTemplate のタイトル TextBox が Bold かつ FontSize=15.5 で InlineTitleTextBox スタイルを維持していること",
                titleTextBox is not null
                && (string?)titleTextBox.Attribute("FontWeight") == "Bold"
                && (string?)titleTextBox.Attribute("FontSize") == "15.5"
                && ((string?)titleTextBox.Attribute("Style"))?.Contains("InlineTitleTextBox", StringComparison.Ordinal) == true,
                titleTextBox?.ToString() ?? "null");
        }

        // 3. TitleBarButtonsPanel で SystemMonitorButton → PullRequestInboxButton → SettingsButton の順に並ぶこと
        var titleBarButtonsPanel = root.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(x + "Name") == "TitleBarButtonsPanel");
        Check(failures, "TitleBarButtonsPanel が存在すること", titleBarButtonsPanel is not null, "TitleBarButtonsPanel");

        if (titleBarButtonsPanel is not null)
        {
            var buttonNames = titleBarButtonsPanel.Elements()
                .Select(e => (string?)e.Attribute(x + "Name") ?? string.Empty)
                .ToList();
            var monitorIdx = buttonNames.IndexOf("SystemMonitorButton");
            var inboxIdx = buttonNames.IndexOf("PullRequestInboxButton");
            var settingsIdx = buttonNames.IndexOf("SettingsButton");
            Check(
                failures,
                "SystemMonitorButton の右隣に PullRequestInboxButton、その右に SettingsButton が配置されていること",
                monitorIdx >= 0 && inboxIdx == monitorIdx + 1 && settingsIdx == inboxIdx + 1,
                string.Join(", ", buttonNames));
        }

        // 4. SystemMonitorOverlay が存在し、メモリ・CPU・ドライブ・フォルダの各表示コントロールを備えていること
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
