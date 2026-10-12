using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public enum ManagedWindowState
{
    Missing,
    Normal,
    Minimized,
    Maximized
}

// HWND ごとの実体の変化を取り込む。旧フォーカスの非同期復元が終わるまでは
// 新旧とも最大化状態なので、最大化「への変化」だけを新しい操作として扱う。
public sealed class WindowFocusSynchronizer(
    Func<IntPtr, ManagedWindowState> readState,
    Func<WindowSlot, int> getMonitorIndex,
    Func<IntPtr, IntPtr, bool> isAbove)
{
    private readonly Dictionary<IntPtr, ManagedWindowState> _observedStates = [];

    public void ObserveWindow(IntPtr handle) => _observedStates[handle] = readState(handle);

    public void Observe(IReadOnlyList<WindowSlot> slots)
    {
        RemoveUnmanagedHandles(slots);
        foreach (var slot in slots.Where(slot => slot.WindowHandle != IntPtr.Zero))
        {
            ObserveWindow(slot.WindowHandle);
        }
    }

    public WindowFocusChanges Reconcile(IReadOnlyList<WindowSlot> slots, bool detectRestores = true)
    {
        RemoveUnmanagedHandles(slots);
        var maximized = new List<WindowSlot>();
        var released = new List<WindowSlot>();
        var tile = new List<WindowSlot>();
        foreach (var slot in slots.Where(slot => slot.WindowHandle != IntPtr.Zero))
        {
            var state = readState(slot.WindowHandle);
            var previous = _observedStates.GetValueOrDefault(slot.WindowHandle, ManagedWindowState.Missing);
            if (!slot.IsFocused && state == ManagedWindowState.Maximized && previous != state)
            {
                maximized.Add(slot);
            }
            else if (slot.IsFocused && (state == ManagedWindowState.Minimized
                || detectRestores && state == ManagedWindowState.Normal && previous == ManagedWindowState.Maximized))
            {
                slot.IsFocused = false;
                released.Add(slot);
                if (state == ManagedWindowState.Normal)
                {
                    tile.Add(slot);
                }
            }

            // アプリ主導の移動・最大化要求が未反映の間は復元判定を保留する。
            if (detectRestores || !slot.IsFocused || state != ManagedWindowState.Normal)
            {
                _observedStates[slot.WindowHandle] = state;
            }
        }

        var focused = new List<WindowSlot>();
        foreach (var group in maximized.GroupBy(getMonitorIndex))
        {
            // 同じ更新間隔で複数窓が最大化された場合は、実際に手前にある窓を採用する。
            var target = group.Aggregate((front, candidate) =>
                isAbove(candidate.WindowHandle, front.WindowHandle) ? candidate : front);
            foreach (var other in slots.Where(slot => slot.IsFocused && getMonitorIndex(slot) == group.Key))
            {
                other.IsFocused = false;
                released.Add(other);
                if (readState(other.WindowHandle) is ManagedWindowState.Normal or ManagedWindowState.Maximized)
                {
                    tile.Add(other);
                }
            }

            // 同時最大化された同じ面の他窓も、新フォーカスの背面で元の象限へ戻す。
            tile.AddRange(group.Where(slot => !ReferenceEquals(slot, target)));
            target.IsFocused = true;
            focused.Add(target);
        }

        // パネルからの切替直後に直接復元・最小化した場合も、まだ背後で最大化している
        // 旧フォーカスの後片付けを引き継ぐ。同じ面以外の窓や最小化窓は対象にしない。
        var changedMonitors = focused.Concat(released).Select(getMonitorIndex).ToHashSet();
        tile.AddRange(slots.Where(slot => !slot.IsFocused && changedMonitors.Contains(getMonitorIndex(slot))
            && readState(slot.WindowHandle) == ManagedWindowState.Maximized));

        return new WindowFocusChanges(focused, released, tile.Distinct().ToArray());
    }

    private void RemoveUnmanagedHandles(IReadOnlyList<WindowSlot> slots)
    {
        var managed = slots.Select(slot => slot.WindowHandle).ToHashSet();
        foreach (var handle in _observedStates.Keys.Where(handle => !managed.Contains(handle)).ToArray())
        {
            _observedStates.Remove(handle);
        }
    }
}

public sealed record WindowFocusChanges(
    IReadOnlyList<WindowSlot> Focused,
    IReadOnlyList<WindowSlot> Released,
    IReadOnlyList<WindowSlot> Tile)
{
    public bool HasChanges => Focused.Count > 0 || Released.Count > 0;
}
