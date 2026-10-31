using System.Windows;

namespace TurtleAIQuartetHub.Panel;

/// <summary>
/// タイトルバーのアイコンボタン（ChromeButton）の右下に「更新あり」の赤丸を出す添付プロパティ。
/// ボタン側で true にするだけで、どのアイコンでも同じ見た目のバッジになる。件数は出さない。
/// </summary>
public static class UpdateBadge
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(UpdateBadge),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
