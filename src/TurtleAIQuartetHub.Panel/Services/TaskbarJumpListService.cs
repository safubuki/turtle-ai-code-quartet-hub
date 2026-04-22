using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class TaskbarJumpListService
{
    public static void Update(IReadOnlyList<WindowSlot> slots, bool compactMode)
    {
        if (Application.Current is null)
        {
            return;
        }

        var appPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        var jumpList = new JumpList
        {
            ShowRecentCategory = false,
            ShowFrequentCategory = false
        };

        foreach (var slot in slots.Take(4))
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = BuildSlotTitle(slot),
                Description = $"スロット{slot.Name}をフォーカス切替し、表示モードも連動します。",
                Arguments = $"--slot-toggle {slot.Name}",
                ApplicationPath = appPath,
                IconResourcePath = appPath,
                CustomCategory = "スロット"
            });
        }

        jumpList.JumpItems.Add(new JumpTask
        {
            Title = compactMode ? "標準表示に戻す" : "縮小表示にする",
            Description = "パネルの表示モードを切り替えます。",
            Arguments = compactMode ? "--mode standard" : "--mode compact",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "表示"
        });

        jumpList.JumpItems.Add(new JumpTask
        {
            Title = "VS Code を最前面へ",
            Description = "管理中の VS Code を前面に寄せます。",
            Arguments = "--layer top",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "配置"
        });

        jumpList.JumpItems.Add(new JumpTask
        {
            Title = "VS Code を最背面へ",
            Description = "管理中の VS Code を背面へ送ります。",
            Arguments = "--layer back",
            ApplicationPath = appPath,
            IconResourcePath = appPath,
            CustomCategory = "配置"
        });

        try
        {
            JumpList.SetJumpList(Application.Current, jumpList);
            jumpList.Apply();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
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

        var status = slot.AiStatus switch
        {
            AiStatus.Running => "実行中",
            AiStatus.Completed => "完了",
            AiStatus.WaitingForConfirmation => "確認中",
            AiStatus.Error => "エラー",
            AiStatus.NeedsAttention => "要確認",
            _ => slot.WindowStatus == SlotWindowStatus.Ready ? "待機中" : "停止中"
        };

        var focusMark = slot.IsFocused ? "● " : string.Empty;
        return $"{focusMark}{slot.Name} {title} [{status}]";
    }
}
