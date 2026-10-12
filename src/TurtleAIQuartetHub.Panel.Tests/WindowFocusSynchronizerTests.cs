using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class WindowFocusSynchronizerTests
{
    public static void Run(List<string> failures)
    {
        Verify("直接最大化するとフォーカスになり、次の周期でも維持される", f =>
        {
            f.State(0, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return f.Slots[0].IsFocused && changes.Focused.Single() == f.Slots[0]
                && !f.Sync.Reconcile(f.Slots).HasChanges;
        }, failures);

        Verify("別窓の直接最大化は同じ面の旧フォーカスだけを4面へ戻す", f =>
        {
            f.Focus(0);
            f.State(1, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return !f.Slots[0].IsFocused && f.Slots[1].IsFocused
                && changes.Tile.SequenceEqual([f.Slots[0]])
                && !f.Sync.Reconcile(f.Slots).HasChanges;
        }, failures);

        Verify("別ディスプレイの最大化とフォーカスには影響しない", f =>
        {
            f.Slots[2].MonitorOverride = 1;
            f.Focus(0);
            f.Focus(2);
            f.State(1, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return f.Slots[1].IsFocused && f.Slots[2].IsFocused
                && changes.Tile.SequenceEqual([f.Slots[0]]);
        }, failures);

        Verify("直接復元するとフォーカスを解除し、自分だけ象限へ戻す", f =>
        {
            f.Focus(0);
            f.State(0, ManagedWindowState.Normal);
            var changes = f.Sync.Reconcile(f.Slots);
            return !f.Slots[0].IsFocused && changes.Tile.SequenceEqual([f.Slots[0]])
                && changes.Focused.Count == 0;
        }, failures);

        Verify("直接最小化では復元する窓を返さない", f =>
        {
            f.Focus(0);
            f.State(0, ManagedWindowState.Minimized);
            var changes = f.Sync.Reconcile(f.Slots);
            return !f.Slots[0].IsFocused && changes.Released.Single() == f.Slots[0]
                && changes.Tile.Count == 0;
        }, failures);

        Verify("他スロットの手動最小化は新フォーカスの整列対象にしない", f =>
        {
            f.Focus(0);
            f.State(2, ManagedWindowState.Minimized);
            f.State(1, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return changes.Tile.SequenceEqual([f.Slots[0]]) && !f.Slots[2].IsFocused;
        }, failures);

        Verify("素早い直接切替で未復元の旧フォーカスへ戻らない", f =>
        {
            f.Focus(0);
            f.State(1, ManagedWindowState.Maximized);
            f.Sync.Reconcile(f.Slots);
            f.State(3, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return f.Slots[3].IsFocused && !f.Slots[0].IsFocused && !f.Slots[1].IsFocused
                && changes.Tile.ToHashSet().SetEquals([f.Slots[0], f.Slots[1]])
                && !f.Sync.Reconcile(f.Slots).HasChanges;
        }, failures);

        Verify("同時最大化は手前の窓を採用し、他の最大化窓を復元する", f =>
        {
            f.State(0, ManagedWindowState.Maximized);
            f.State(1, ManagedWindowState.Maximized);
            var changes = f.Sync.Reconcile(f.Slots);
            return f.Slots[1].IsFocused && !f.Slots[0].IsFocused
                && changes.Tile.SequenceEqual([f.Slots[0]]);
        }, failures);

        Verify("ディスプレイ移動中の復元判定を保留し、後で取り込む", f =>
        {
            f.Focus(0);
            f.State(0, ManagedWindowState.Normal);
            var deferred = f.Sync.Reconcile(f.Slots, detectRestores: false);
            var changes = f.Sync.Reconcile(f.Slots);
            return !deferred.HasChanges && !f.Slots[0].IsFocused && changes.Tile.Count == 1;
        }, failures);

        Verify("パネルの復元要求が未反映でも旧フォーカスを再採用しない", f =>
        {
            f.Focus(0);
            f.Sync.Observe(f.Slots);
            f.Slots[0].IsFocused = false;
            return !f.Sync.Reconcile(f.Slots).HasChanges;
        }, failures);

        Verify("パネル切替直後の手動最小化でも旧フォーカスの復元を引き継ぐ", f =>
        {
            f.Focus(0);
            f.Slots[0].IsFocused = false;
            f.Focus(1);
            f.State(1, ManagedWindowState.Minimized);
            var changes = f.Sync.Reconcile(f.Slots);
            return !f.Slots[1].IsFocused && changes.Tile.SequenceEqual([f.Slots[0]]);
        }, failures);

        Verify("閉じた窓や割当解除したHWNDを再採用しない", f =>
        {
            f.Focus(0);
            f.State(0, ManagedWindowState.Missing);
            f.Slots[0].ClearWindow();
            return !f.Sync.Reconcile(f.Slots).HasChanges && !f.Slots[0].IsFocused;
        }, failures);
    }

    private static void Verify(string name, Func<Fixture, bool> check, List<string> failures)
    {
        try
        {
            if (!check(new Fixture())) failures.Add(name);
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.Message}");
        }
    }

    private sealed class Fixture
    {
        private readonly Dictionary<IntPtr, ManagedWindowState> _states = [];
        public WindowSlot[] Slots { get; } = Enumerable.Range(0, 4).Select(index =>
            new WindowSlot(new SlotConfig { Name = ((char)('A' + index)).ToString() })
            { WindowHandle = new IntPtr(index + 1) }).ToArray();
        public WindowFocusSynchronizer Sync { get; }

        public Fixture()
        {
            foreach (var slot in Slots) _states[slot.WindowHandle] = ManagedWindowState.Normal;
            Sync = new WindowFocusSynchronizer(
                handle => _states.GetValueOrDefault(handle, ManagedWindowState.Missing),
                slot => slot.MonitorOverride ?? 0,
                (candidate, other) => candidate.ToInt64() > other.ToInt64());
            Sync.Observe(Slots);
        }

        public void State(int index, ManagedWindowState state) => _states[Slots[index].WindowHandle] = state;
        public void Focus(int index)
        {
            State(index, ManagedWindowState.Maximized);
            Slots[index].IsFocused = true;
            Sync.Observe(Slots);
        }
    }
}
