using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenIsland.App;

/// <summary>
/// 字符串颜色转换为Brush
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorString));
            }
            catch
            {
                return Brushes.Gray;
            }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Bool转Brush（用于连接状态指示）
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool connected)
        {
            return connected ? Brushes.Green : Brushes.Red;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Bool转Visibility
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 反向 Bool 转 Visibility（true → Collapsed，false → Visible）。
/// 用于"在权限模式下隐藏普通会话列表，反之显示"这种互斥切换。
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 热力图强度（0-4） → Brush。0=空格灰，1-4=渐深蓝。
/// </summary>
public class HeatmapIntensityToBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C)), // 0 空
        new SolidColorBrush(Color.FromRgb(0x1F, 0x37, 0x59)), // 1 极淡
        new SolidColorBrush(Color.FromRgb(0x32, 0x5B, 0x99)), // 2 中淡
        new SolidColorBrush(Color.FromRgb(0x4A, 0x82, 0xCC)), // 3 中深
        new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xE0))  // 4 最深
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int i = value is int ii ? Math.Clamp(ii, 0, Palette.Length - 1) : 0;
        return Palette[i];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>0-23 小时 → "12 AM" / "3 PM" / "—"。-1 表示没数据。</summary>
public class PeakHourTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int h || h < 0 || h > 23) return "—";
        if (h == 0) return "12 AM";
        if (h == 12) return "12 PM";
        return h < 12 ? $"{h} AM" : $"{h - 12} PM";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>大数字人类化：45_500_000 → "45.5M"，30_298 → "30,298"。</summary>
public class HumanNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        ulong n = value switch
        {
            ulong u => u,
            uint ui => ui,
            int i => (ulong)Math.Max(0, i),
            long l => (ulong)Math.Max(0L, l),
            _ => 0UL
        };
        if (n >= 1_000_000_000UL) return $"{n / 1_000_000_000.0:0.#}B";
        if (n >= 1_000_000UL) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 10_000UL) return $"{n / 1_000.0:0.#}k";
        return n.ToString("N0", CultureInfo.InvariantCulture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
