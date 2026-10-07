using System.Globalization;
using System.Windows.Data;

namespace OfficeAssistant.App.Infrastructure;

/// <summary>把 0–100 的百分比（或 0–1 的比例）夹取到进度条可用范围。</summary>
public sealed class ClampPercentConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0d,
        };

        // ProgressBar 用 0–100：大于 1 的值视为已是百分比
        if (number <= 1d) number *= 100d;
        return Math.Clamp(number, 0d, 100d);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反（用于"未展开时才显示展开点"这类可见性判断）。</summary>
public sealed class InverseBoolToVisibilityConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把资源键（如 Brush.Blue）换成画刷，供摘要卡的 4 色占比条使用。</summary>
public sealed class ResourceBrushConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) return System.Windows.Media.Brushes.Transparent;

        // 依次在应用资源与系统资源里找；找不到就退化成透明而不是抛异常，
        // 避免一个配色键写错就让整个摘要卡渲染失败。
        return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把百分比换算成占比条的实际像素宽度（占比条总宽 248px）。</summary>
public sealed class PercentToWidthConverter : WpfIValueConverter
{
    /// <summary>摘要卡内宽 280 - 左右内边距 14×2 = 252，留 4px 给间隔。</summary>
    private const double TotalWidth = 248;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d,
        };

        // 极小占比也给 2px，否则"有这一类"却在色条上看不见
        return Math.Max(percent <= 0 ? 0 : 2, TotalWidth * percent / 100d);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
