using System.Windows;

namespace TurtleAIQuartetHub.Panel;

/// <summary>
/// 縮小表示のときだけ詰めた見た目にする部品へ伝える添付プロパティ（子へ継承する）。
/// 今は控え一覧（StoredOverviewRoot）に設定し、タイルを名前だけの低い表示に切り替える。
/// </summary>
public static class CompactLayout
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(CompactLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
