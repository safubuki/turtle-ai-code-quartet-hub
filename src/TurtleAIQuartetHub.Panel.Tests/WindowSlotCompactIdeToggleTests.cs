using TurtleAIQuartetHub.Panel.Models;

internal static class WindowSlotCompactIdeToggleTests
{
    public static void Run(List<string> failures)
    {
        Verify("VS Code と Antigravity は同じボタンで相互に切り替わる", () =>
        {
            var slot = CreateSlot(AppConfig.VsCodeApplicationId);
            var vsCodeState = slot.CompactIdeToggleLabel == "V"
                && slot.CompactIdeToggleTargetApplicationId == "antigravity"
                && slot.CanToggleCompactIde;

            slot.ApplicationId = "antigravity";
            return vsCodeState
                && slot.CompactIdeToggleLabel == "A"
                && slot.CompactIdeToggleTargetApplicationId == AppConfig.VsCodeApplicationId
                && slot.CanToggleCompactIde;
        }, failures);

        Verify("切り替え先が未検出ならトグルを無効化する", () =>
        {
            var slot = CreateSlot(AppConfig.VsCodeApplicationId);
            slot.IsAntigravityAvailable = false;
            var cannotSwitchToAntigravity = !slot.CanToggleCompactIde;

            slot.ApplicationId = "antigravity";
            return cannotSwitchToAntigravity && slot.CanToggleCompactIde;
        }, failures);

        Verify("CLI 選択中は中立表示にし、利用可能な IDE を選ぶ", () =>
        {
            var slot = CreateSlot("codex");
            var prefersVsCode = slot.CompactIdeToggleLabel == "·"
                && !slot.IsCompactIdeSelected
                && slot.CompactIdeToggleTargetApplicationId == AppConfig.VsCodeApplicationId;

            slot.IsVsCodeAvailable = false;
            return prefersVsCode
                && slot.CompactIdeToggleTargetApplicationId == "antigravity"
                && slot.CanToggleCompactIde;
        }, failures);

        Verify("選択と検出状態の変更をボタンのバインディングへ通知する", () =>
        {
            var slot = CreateSlot(AppConfig.VsCodeApplicationId);
            var changed = new HashSet<string>();
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null)
                {
                    changed.Add(e.PropertyName);
                }
            };

            slot.ApplicationId = "antigravity";
            var selectionNotified = changed.Contains(nameof(WindowSlot.CompactIdeToggleLabel))
                && changed.Contains(nameof(WindowSlot.CompactIdeToggleTargetApplicationId))
                && changed.Contains(nameof(WindowSlot.CompactIdeToggleToolTip));

            changed.Clear();
            slot.IsVsCodeAvailable = false;
            return selectionNotified
                && changed.Contains(nameof(WindowSlot.CanToggleCompactIde))
                && changed.Contains(nameof(WindowSlot.CompactIdeToggleToolTip));
        }, failures);
    }

    private static WindowSlot CreateSlot(string applicationId) => new(new SlotConfig
    {
        Name = "A",
        ApplicationId = applicationId
    })
    {
        IsVsCodeAvailable = true,
        IsAntigravityAvailable = true
    };

    private static void Verify(string name, Func<bool> assertion, List<string> failures)
    {
        try
        {
            if (!assertion())
            {
                failures.Add(name);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }
}
