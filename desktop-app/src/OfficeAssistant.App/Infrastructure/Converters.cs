using System.Globalization;
using System.Windows.Media.Imaging;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.Infrastructure;

/// <summary>字节数 → 人类可读文本。</summary>
public sealed class BytesConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            long l => Format.Bytes(l),
            int i => Format.Bytes(i),
            ulong u => Format.Bytes((long)u),
            _ => "—",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>Unix 秒 → 本地时间文本。</summary>
public sealed class UnixTimeConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long seconds || seconds <= 0) return "—";
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm"); }
        catch { return "—"; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>DateTime → 文本。</summary>
public sealed class DateTimeConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>枚举 → 中文文本。</summary>
public sealed class EnumTextConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Caution => "谨慎",
        RiskLevel.Dangerous => "危险",
        FileCategory c => RecognitionText.Describe(c),
        HomeworkState s => s switch
        {
            HomeworkState.LocalDone => "本地已完成",
            HomeworkState.Submitted => "已提交",
            HomeworkState.Overdue => "已逾期",
            HomeworkState.NotSubmitted => "未提交",
            _ => "状态未知",
        },
        bool b => b ? "是" : "否",
        _ => value?.ToString() ?? "—",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>风险等级 → 颜色画刷。</summary>
public sealed class RiskBrushConverter : WpfIValueConverter
{
    private static readonly WpfSolidColorBrush Safe = Freeze("#1B7F3B");
    private static readonly WpfSolidColorBrush Caution = Freeze("#B26B00");
    private static readonly WpfSolidColorBrush Dangerous = Freeze("#C42B1C");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        RiskLevel.Safe => Safe,
        RiskLevel.Caution => Caution,
        RiskLevel.Dangerous => Dangerous,
        _ => WpfBrushes.Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;

    private static WpfSolidColorBrush Freeze(string hex)
    {
        var brush = new WpfSolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>作业状态 → 颜色。</summary>
public sealed class StateBrushConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        HomeworkState.Overdue => new WpfSolidColorBrush(WpfColor.FromRgb(0xC4, 0x2B, 0x1C)),
        HomeworkState.NotSubmitted => new WpfSolidColorBrush(WpfColor.FromRgb(0xB2, 0x6B, 0x00)),
        HomeworkState.Submitted or HomeworkState.LocalDone => new WpfSolidColorBrush(WpfColor.FromRgb(0x1B, 0x7F, 0x3B)),
        _ => new WpfSolidColorBrush(WpfColor.FromRgb(0x60, 0x60, 0x60)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>布尔取反；当 ConverterParameter 为 "vis" 时返回 Visibility（用于"未识别"标记的显示）。</summary>
public sealed class InverseBoolConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var inverted = value is bool b && !b;
        if (parameter is string s && s.Equals("vis", StringComparison.OrdinalIgnoreCase))
            return inverted ? Visibility.Visible : Visibility.Collapsed;
        return inverted;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>bool → Visibility。</summary>
public sealed class BoolToVisibilityConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>缩略图文件路径 → 可绑定的图片源（带缓存，避免重复解码）。</summary>
public sealed class ThumbnailConverter : WpfIValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        BitmapImage? image = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch
        {
            image = null;
        }

        lock (Gate)
        {
            if (Cache.Count > 2000) Cache.Clear();
            Cache[path] = image;
        }
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>把 0–100 的百分比转成进度条可用值。</summary>
public sealed class PercentConverter : WpfIValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            double d => Math.Clamp(d, 0, 100),
            _ => 0d,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => WpfBinding.DoNothing;
}

/// <summary>类别中文名（供视图与其他转换器复用）。</summary>
public static class RecognitionText
{
    public static string Describe(FileCategory category) => category switch
    {
        FileCategory.Document => "文档",
        FileCategory.Spreadsheet => "表格",
        FileCategory.Presentation => "演示文稿",
        FileCategory.Pdf => "PDF",
        FileCategory.Text => "文本",
        FileCategory.Image => "图片",
        FileCategory.Audio => "音频",
        FileCategory.Video => "视频",
        FileCategory.Archive => "压缩包/镜像",
        FileCategory.Executable => "可执行程序",
        FileCategory.Installer => "安装包",
        FileCategory.Code => "代码/脚本",
        FileCategory.Font => "字体",
        FileCategory.Database => "数据库",
        FileCategory.Shortcut => "快捷方式",
        FileCategory.System => "系统文件",
        FileCategory.Other => "其他",
        _ => "未识别",
    };
}