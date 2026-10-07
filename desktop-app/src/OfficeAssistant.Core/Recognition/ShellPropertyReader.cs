using System.Globalization;
using System.Runtime.InteropServices;

namespace OfficeAssistant.Core.Recognition;

/// <summary>
/// Windows Shell 扩展属性读取（音视频时长/分辨率、文档标题作者等）。
///
/// 实现方式：<c>Shell.Application</c> 的 Folder.GetDetailsOf —— 系统自带能力，
/// 覆盖 mp3/mp4/mkv/avi/wmv 等常见格式的时长与分辨率，无需 ffprobe，也不引入第三方库。
/// 对应《功能说明》"音视频读取时长、分辨率等元信息（调用系统属性或可选 ffprobe）"。
/// 该路径完全失败时，调用方降级为按文件头解析（图片尺寸、JPG/PNG 等）。
/// </summary>
public static class ShellPropertyReader
{
    private static readonly object Gate = new();
    private static bool _unavailable;

    private static readonly Dictionary<string, int> IndexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按中文/英文属性显示名读取属性值（如 "时长"、"长度"、"Duration"）。</summary>
    public static string? GetProperty(string filePath, params string[] candidateNames)
    {
        if (_unavailable) return null;
        try
        {
            dynamic? shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            if (shell is null) return null;
            try
            {
                var folderPath = Path.GetDirectoryName(filePath);
                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(folderPath) || string.IsNullOrEmpty(fileName)) return null;

                dynamic folder = shell.NameSpace(folderPath);
                if (folder is null)
                {
                    MarkUnavailable();
                    return null;
                }

                dynamic item = folder.ParseName(fileName);
                if (item is null) return null;

                foreach (var name in candidateNames)
                {
                    var index = ResolveIndex(folder, name);
                    if (index < 0) continue;
                    string? value = folder.GetDetailsOf(item, index);
                    if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
                }
                return null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            MarkUnavailable();
            return null;
        }
    }

    private static void MarkUnavailable()
    {
        lock (Gate) _unavailable = true;
    }

    /// <summary>把属性显示名解析为列号（按系统语言探测一次并缓存）。</summary>
    private static int ResolveIndex(dynamic folder, string name)
    {
        lock (Gate)
        {
            if (IndexCache.TryGetValue(name, out var cached)) return cached;
            if (NameCache.TryGetValue(name, out var cachedValue))
            {
                var parsed = int.TryParse(cachedValue, out var v) ? v : -1;
                IndexCache[name] = parsed;
                return parsed;
            }
        }

        var found = -1;
        try
        {
            for (var i = 0; i < 400; i++)
            {
                string? column = folder.GetDetailsOf(null, i);
                if (string.IsNullOrEmpty(column)) continue;
                lock (Gate)
                {
                    NameCache[column!] = i.ToString(CultureInfo.InvariantCulture);
                }
                if (string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
        }
        catch
        {
            found = -1;
        }

        lock (Gate) IndexCache[name] = found;
        return found;
    }

    /// <summary>尝试解析形如 "00:03:21" 或 "1:02:03" 的时长文本。</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text.Trim();
        if (TimeSpan.TryParse(cleaned, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
            return ts;

        // 某些语言下为 "3 分 21 秒"
        var minutes = 0;
        var seconds = 0;
        var mIdx = cleaned.IndexOf('分');
        if (mIdx > 0 && int.TryParse(cleaned[..mIdx].Trim(), out var m)) minutes = m;
        var sStart = mIdx >= 0 ? mIdx + 1 : 0;
        var sIdx = cleaned.IndexOf('秒', sStart);
        if (sIdx > sStart && int.TryParse(cleaned[sStart..sIdx].Trim(), out var s)) seconds = s;
        var total = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return total > TimeSpan.Zero ? total : null;
    }

    /// <summary>尝试解析形如 "1920 x 1080" 或 "1920x1080" 的分辨率文本。</summary>
    public static (int Width, int Height)? ParseResolution(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(['x', 'X', '×', '*'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        if (int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return null;
    }
}
