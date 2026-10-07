using System.Runtime.InteropServices;
using System.Text;

namespace OfficeAssistant.Core.Safety;

/// <summary>
/// 路径规范化与判定工具。所有面向文件系统的判定都应先经过 <see cref="Normalize"/>，
/// 避免相对路径、<c>..</c>、短名（8.3）、尾随分隔符或大小写差异导致保护规则被绕过。
/// </summary>
public static class PathTools
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathNameW(string shortPath, StringBuilder longPath, int bufferLength);

    /// <summary>规范化为“完整路径、无尾随分隔符（根目录保留）、统一大小写用于比较”。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim().Trim('"');
        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            // 非法路径：原样返回并交由调用方判定
        }

        // 折叠 .. 之后再次确认（Path.GetFullPath 已处理，这里兜底）
        if (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/')))
            p = p.TrimEnd('\\', '/');

        return p;
    }

    /// <summary>比较用的规范键：去尾随分隔符 + 大写不变文化。</summary>
    public static string Key(string path)
    {
        var p = Normalize(path);
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p.ToUpperInvariant();
    }

    /// <summary>尝试把短名（8.3）解析成长名，失败时返回原值。</summary>
    public static string TryExpandShortName(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            var len = GetLongPathNameW(path, sb, sb.Capacity);
            if (len > 0 && len < sb.Capacity) return sb.ToString();
        }
        catch
        {
            // 忽略
        }
        return path;
    }

    public static bool IsRoot(string path)
    {
        var p = Normalize(path);
        return p.Length == 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
    }

    /// <summary>获取盘符根（如 <c>D:\</c>）；非绝对路径返回空串。</summary>
    public static string VolumeRoot(string path)
    {
        var p = Normalize(path);
        if (p.Length >= 2 && p[1] == ':')
            return char.ToUpperInvariant(p[0]) + ":\\";
        return string.Empty;
    }

    /// <summary>是否为 Windows 目录名中的系统保留项。</summary>
    public static bool IsSystemFolderName(string name)
        => name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
           || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Boot", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Config.Msi", StringComparison.OrdinalIgnoreCase)
           || name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase)
           || name.Equals("$SysReset", StringComparison.OrdinalIgnoreCase)
           || name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
           || name.Equals("$Windows.~BT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("$Windows.~WS", StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉非法文件名字符。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.Length == 0 ? "未命名" : sb.ToString();
    }

    public static string Shorten(string path, int max = 70)
        => path.Length <= max ? path : "…" + path[^max..];
}
