using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TokenMonitor.App.Infrastructure;

/// <summary>乘法转换器：values[0]（宽/长）× values[1]（比率）。用于占比条填充宽度。</summary>
public sealed class MultiplyConverter : IMultiValueConverter
{
    public static readonly MultiplyConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double acc = 1;
        foreach (var v in values)
        {
            if (v is double d) acc *= d;
            else if (v is int i) acc *= i;
            else if (v is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) acc *= p;
            else return 0d;
        }
        return acc;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（true=Visible；可配 ! 前缀反转）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>bool 取反（互斥 RadioButton 共用一个 bool：一个绑原值、一个绑反值）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>null/空字符串 → Collapsed，否则 Visible。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is string s ? s.Length > 0 : value is not null;
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>数值 > 0 → Visible。</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is long l && l > 0 || value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>主面板宽 > 900 时 3 列（03-ui-spec §1.2）。</summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double w && w > 900 ? 3 : 2;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举与字符串参数相等 → true（数据范围菜单勾选态）。
/// ConvertBack：勾选时把参数名写回枚举（MenuItem.IsChecked 默认 TwoWay，
/// 此前抛 NotSupportedException 导致范围选择静默失效）；取消勾选不动——
/// 单选互斥由下一次勾选覆盖。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString()?.Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase) == true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null && targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
        return System.Windows.Data.Binding.DoNothing;
    }
}

/// <summary>窗口透明度：0-255 int ↔ 0-1 double（悬浮球透明度托盘联动）。</summary>
public sealed class OpacityByteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int b && b > 0 ? b / 255.0 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (int)Math.Round((value is double d ? d : 1.0) * 255);
}
