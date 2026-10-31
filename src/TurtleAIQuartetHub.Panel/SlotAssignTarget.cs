using System.Windows;

namespace TurtleAIQuartetHub.Panel;

/// <summary>
/// 縮小表示で控えカードを選んでいる間、スロット A〜D を「ここに表示」の配置先に切り替える添付プロパティ。
/// 縮小表示のスロット欄（CompactBarPanel）に設定すると子へ継承され、各スロットのテンプレートが見た目を切り替える。
/// </summary>
public static class SlotAssignTarget
{
    public static readonly DependencyProperty IsArmedProperty = DependencyProperty.RegisterAttached(
        "IsArmed",
        typeof(bool),
        typeof(SlotAssignTarget),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsArmed(DependencyObject element) => (bool)element.GetValue(IsArmedProperty);

    public static void SetIsArmed(DependencyObject element, bool value) => element.SetValue(IsArmedProperty, value);
}
