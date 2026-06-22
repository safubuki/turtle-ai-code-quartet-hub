using System.Globalization;
using System.Windows.Data;

namespace TurtleAIQuartetHub.Panel;

// double 値から ConverterParameter（既定 0）を引いて返す。オーバーレイのダイアログ高さを
// ウィンドウの実クライアント高に追従させ、上下マージン分だけ縮めて常に画面内へ収めるために使う。
// 入力やパラメータが数値でないときは元値（または 0）を返し、レイアウトを壊さない。
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double input)
        {
            return value;
        }

        var amount = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0.0
        };

        var result = input - amount;
        return result < 0 ? 0.0 : result;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
