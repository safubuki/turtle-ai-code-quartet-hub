using System.IO;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class PanelOrderTests
{
    public static void Run(List<string> failures)
    {
        Verify("表の下方向への挿入で中間カードと実行情報を保持する", store =>
        {
            var source = store.Slots[0];
            source.WindowHandle = new IntPtr(42);
            source.WindowTitle = "実行中";
            source.WindowStatus = SlotWindowStatus.Ready;
            source.IsFocused = true;
            source.IsHidden = true;
            source.MonitorOverride = 2;
            source.WindowLayerMode = WindowSlot.SlotWindowLayerMode.Backmost;
            var layout = new VscodeLayoutPreference();
            source.PreferredLayout = layout;

            return store.MoveSlotContents(source, store.Slots[3])
                && Titles(store) == "B,C,D,A"
                && string.Join(",", store.Slots.Select(slot => slot.Name)) == "A,B,C,D"
                && string.Join(",", store.Slots.Select(slot => slot.RuntimeSlotName)) == "B,C,D,A"
                && store.Slots[3].WindowHandle == new IntPtr(42)
                && store.Slots[3].WindowTitle == "実行中"
                && store.Slots[3].WindowStatus == SlotWindowStatus.Ready
                && store.Slots[3].IsFocused && store.Slots[3].IsHidden
                && store.Slots[3].MonitorOverride == 2
                && store.Slots[3].WindowLayerMode == WindowSlot.SlotWindowLayerMode.Backmost
                && ReferenceEquals(store.Slots[3].PreferredLayout, layout)
                && store.Slots[3].SavedWorkspacePath == @"C:\order\A";
        }, failures);

        Verify("表の上方向への挿入と既存交換が異なる並びを作る", store =>
        {
            store.MoveSlotContents(store.Slots[3], store.Slots[0]);
            var inserted = Titles(store) == "D,A,B,C";
            store.SwapSlotContents(store.Slots[0], store.Slots[3]);
            return inserted && Titles(store) == "C,A,B,D";
        }, failures);

        Verify("同じ位置・一覧外への移動は状態を変更しない", store =>
        {
            var before = File.ReadAllText(Path.Combine(store.Config.StateDirectory, "slots.json"));
            return !store.MoveSlotContents(store.Slots[0], store.Slots[0])
                && !store.MoveSlotContents(new WindowSlot(new SlotConfig { Name = "X" }), store.Slots[0])
                && !store.MoveStoredPanelContents(store.StoredPanels[0], new StoredPanelSlot(100))
                && File.ReadAllText(Path.Combine(store.Config.StateDirectory, "slots.json")) == before;
        }, failures);

        Verify("控えのページまたぎ挿入で順序とページへの反映を保持する", store =>
        {
            var firstPageSlot = store.StoredPanelPages[0].Slots[1];
            var secondPageSlot = store.StoredPanelPages[1].Slots[2];
            return store.MoveStoredPanelContents(store.StoredPanels[1], store.StoredPanels[6])
                && string.Join(",", store.StoredPanels.Take(8).Select(slot => slot.PanelTitle)) == "1,3,4,5,6,7,2,8"
                && ReferenceEquals(firstPageSlot, store.StoredPanels[1]) && firstPageSlot.PanelTitle == "3"
                && ReferenceEquals(secondPageSlot, store.StoredPanels[6]) && secondPageSlot.PanelTitle == "2"
                && secondPageSlot.WorkspacePath == @"C:\order\stored-2"
                && secondPageSlot.ApplicationId == "antigravity";
        }, failures);

        Verify("控えの空き位置への移動と上方向への挿入が内容を失わない", store =>
        {
            store.MoveStoredPanelContents(store.StoredPanels[0], store.StoredPanels[9]);
            var movedToEmpty = store.StoredPanels[9].PanelTitle == "1" && !store.StoredPanels[8].HasContent;
            store.MoveStoredPanelContents(store.StoredPanels[9], store.StoredPanels[0]);
            return movedToEmpty
                && string.Join(",", store.StoredPanels.Take(8).Select(slot => slot.PanelTitle)) == "1,2,3,4,5,6,7,8"
                && !store.StoredPanels[8].HasContent && !store.StoredPanels[9].HasContent;
        }, failures);

        Verify("並び替えとタイトル変更を再読み込みして復元する", store =>
        {
            store.MoveSlotContents(store.Slots[0], store.Slots[2]);
            store.MoveStoredPanelContents(store.StoredPanels[7], store.StoredPanels[3]);
            store.Slots[2].PanelTitle = "編集済みカード";
            store.StoredPanels[3].PanelTitle = "編集済み控え";
            var restored = new StatusStore(store.Config);
            return Titles(restored) == "B,C,編集済みカード,D"
                && restored.Slots[2].RuntimeSlotName == "A"
                && restored.Slots[2].SavedWorkspacePath == @"C:\order\A"
                && restored.StoredPanels[3].PanelTitle == "編集済み控え"
                && restored.StoredPanels[3].WorkspacePath == @"C:\order\stored-8"
                && restored.StoredPanels[4].PanelTitle == "4";
        }, failures);
    }

    private static string Titles(StatusStore store) => string.Join(",", store.Slots.Select(slot => slot.PanelTitle));

    private static void Verify(string name, Func<StatusStore, bool> assertion, List<string> failures)
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "TurtlePanelOrderTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StatusStore(new AppConfig { StateDirectory = stateDirectory });
            foreach (var slot in store.Slots)
            {
                slot.PanelTitle = slot.Name;
                slot.Path = slot.SavedWorkspacePath = @"C:\order\" + slot.Name;
                slot.SavedWorkspaceConfirmed = true;
            }
            foreach (var panel in store.StoredPanels.Take(8))
            {
                panel.LoadFrom(panel.Label, @"C:\order\stored-" + panel.Label, "antigravity");
            }
            if (!assertion(store)) failures.Add(name);
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }
}
