using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class TaskbarJumpListService
{
    private static string? _lastSignature;
    private static string? _cachedAppPath;

    public static void Update(
        IReadOnlyList<WindowSlot> slots,
        DisplayMode displayMode,
        IReadOnlyList<LauncherApplication> workspaceApplications,
        IReadOnlyList<LauncherApplication> auxiliaryApplications)
    {
        var appPath = GetCurrentAppPath();
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        var managedSlots = slots.Take(4).ToList();
        var visibleSlots = managedSlots
            .Where(slot => slot.WindowStatus != SlotWindowStatus.Missing)
            .ToList();
        var allSlotsStopped = managedSlots.Count > 0
            && managedSlots.All(slot => slot.WindowStatus == SlotWindowStatus.Missing);

        var signature = BuildSignature(
            managedSlots,
            displayMode,
            isActiveMenu: true,
            workspaceApplications,
            auxiliaryApplications);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var jumpList = CreateBaseJumpList();

        if (visibleSlots.Count > 0)
        {
            foreach (var slot in visibleSlots)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = BuildSlotTitle(slot),
                    Description = $"スロット {slot.Name} の管理中ウィンドウを切り替えます。",
                    Arguments = $"--slot-toggle {slot.Name}",
                    ApplicationPath = appPath,
                    IconResourcePath = appPath,
                    CustomCategory = "スロット"
                });
            }
        }
        else if (allSlotsStopped)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "Launch Quartet（一括起動）",
                Description = "各スロットで選択されているアプリで一括起動します。",
                Arguments = "--launch-all",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "起動"
            });
        }

        foreach (var application in auxiliaryApplications.Where(app => app.IsAvailable))
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = $"{application.DisplayName} を開く",
                Description = $"{application.DisplayName} の起動コマンドを送信します。",
                Arguments = $"--launch-app {application.Id}",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "アプリ"
            });
        }

        var (nextModeTitle, nextModeArgs) = displayMode switch
        {
            DisplayMode.Standard => ("縮小表示にする", "--mode compact"),
            DisplayMode.Compact => ("極小表示にする", "--mode micro"),
            DisplayMode.Micro => ("標準表示に戻す", "--mode standard"),
            _ => ("縮小表示にする", "--mode compact")
        };
        jumpList.JumpItems.Add(new JumpTask
        {
            Title = nextModeTitle,
            Description = "パネルの表示モードを切り替えます。",
            Arguments = nextModeArgs,
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "表示"
        });

        if (displayMode != DisplayMode.Standard)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "アプリを探す",
                Description = "現在のパネル位置を強調して知らせます。",
                Arguments = "--locate",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "表示"
            });
        }

        if (displayMode != DisplayMode.Standard)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = "標準表示に戻す",
                Description = "パネルを標準サイズで開きます。",
                Arguments = "--mode standard",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "表示"
            });
        }

        Apply(jumpList, signature);
    }

    public static void SetInactiveMenu()
    {
        var appPath = GetCurrentAppPath();
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        var signature = BuildSignature([], DisplayMode.Standard, isActiveMenu: false);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var jumpList = CreateBaseJumpList();
        jumpList.JumpItems.Add(new JumpTask
        {
            Title = "Turtle AI Code Quartet Hub を起動",
            Description = "アプリが起動していないときに Turtle AI Code Quartet Hub を開きます。",
            Arguments = "--activate",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "起動"
        });

        Apply(jumpList, signature);
    }

    private static JumpList CreateBaseJumpList()
    {
        return new JumpList
        {
            ShowRecentCategory = false,
            ShowFrequentCategory = false
        };
    }

    private static string? GetCurrentAppPath()
    {
        if (!string.IsNullOrWhiteSpace(_cachedAppPath))
        {
            return _cachedAppPath;
        }

        if (Application.Current is null)
        {
            return null;
        }

        using var currentProcess = Process.GetCurrentProcess();
        _cachedAppPath = currentProcess.MainModule?.FileName;
        return _cachedAppPath;
    }

    private static void Apply(JumpList jumpList, string signature)
    {
        if (Application.Current is null)
        {
            return;
        }

        try
        {
            JumpList.SetJumpList(Application.Current, jumpList);
            jumpList.Apply();
            _lastSignature = signature;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static string BuildSignature(
        IEnumerable<WindowSlot> slots,
        DisplayMode displayMode,
        bool isActiveMenu,
        IReadOnlyList<LauncherApplication>? workspaceApplications = null,
        IReadOnlyList<LauncherApplication>? auxiliaryApplications = null)
    {
        var slotSignature = string.Join(
            "|",
            slots.Select(slot => $"{slot.Name}:{slot.WindowStatus}:{slot.ApplicationId}:{slot.DisplayTitle}:{slot.IsFocused}"));
        var appSignature = string.Join(
            "|",
            (workspaceApplications ?? []).Concat(auxiliaryApplications ?? [])
            .Select(app => $"{app.Id}:{app.IsAvailable}:{app.IsSelected}"));
        return $"{isActiveMenu}:{displayMode}:{slotSignature}:{appSignature}";
    }

    private static string BuildSlotTitle(WindowSlot slot)
    {
        var title = slot.DisplayTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"スロット {slot.Name}";
        }

        if (title.Length > 18)
        {
            title = $"{title[..17]}…";
        }

        var status = GetSlotStatusTag(slot);
        return string.IsNullOrEmpty(status)
            ? $"{slot.Name} {title}"
            : $"{slot.Name} {title} [{status}]";
    }

    private static string GetSlotStatusTag(WindowSlot slot)
    {
        if (slot.WindowStatus == SlotWindowStatus.Launching)
        {
            return "起動中";
        }

        return slot.IsFocused ? "表示" : string.Empty;
    }
}
