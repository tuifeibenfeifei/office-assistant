using System.Globalization;

namespace OfficeAssistant.Core.Utils;

/// <summary>展示用格式化helper。</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long value)
    {
        if (value < 0) return "-";
        double v = value;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{value} B" : $"{v:0.##} {Units[i]}";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:0} 毫秒";
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0.#} 秒";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
    }

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(seconds));

    public static string DateTimeText(DateTime? value)
        => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    public static string Percent(double value) => $"{value:0.#}%";

    public static string Rate(double perSecond) => $"{perSecond:0} 项/秒";
}
