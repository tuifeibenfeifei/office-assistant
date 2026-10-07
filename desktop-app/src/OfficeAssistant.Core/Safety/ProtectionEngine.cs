using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;

namespace OfficeAssistant.Core.Safety;

/// <summary>
/// 白名单保护引擎（误删防护的第一道闸门）。
///
/// 默认保护：Windows 目录、Program Files / Program Files (x86)、ProgramData、
/// 用户配置文件根与其下的桌面/文档/图片/音乐/视频/下载/收藏夹/OneDrive，
/// 以及各卷的 $Recycle.Bin、System Volume Information、Recovery 等系统目录。
/// 用户在设置里追加的白名单模式同样参与判定。
///
/// 判定顺序：白名单命中 → 系统目录命中 → 危险扩展 → 所在位置 → 规则库兜底。
/// 任何"危险"级条目都不会进入一键清理清单；即使被显式选中删除，也要通过二次确认。
/// </summary>
public sealed class ProtectionEngine
{
    private readonly List<string> _protectedExact = [];
    private readonly List<string> _protectedPrefix = [];
    private readonly List<string> _userPatterns = [];
    private readonly object _gate = new();

    public ProtectionEngine(Database? db = null)
    {
        LoadSystemDefaults();
        if (db is not null) LoadUserWhitelist(db);
    }

    public IReadOnlyList<string> ProtectedRoots => _protectedPrefix;

    public IReadOnlyList<string> UserPatterns
    {
        get { lock (_gate) return _userPatterns.ToList(); }
    }

    private void LoadSystemDefaults()
    {
        void AddDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var key = PathTools.Key(dir);
            if (!_protectedExact.Contains(key)) _protectedExact.Add(key);
            var prefix = key.EndsWith('\\') ? key : key + "\\";
            if (!_protectedPrefix.Contains(prefix)) _protectedPrefix.Add(prefix);
        }

        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.System));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Favorites));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Templates));
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

        foreach (var drive in SafeDrives())
        {
            foreach (var name in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "Config.Msi" })
                AddDir(Path.Combine(drive, name));
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }
        foreach (var d in drives)
        {
            string root;
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                root = d.RootDirectory.FullName;
            }
            catch { continue; }
            yield return root;
        }
    }
    private void LoadUserWhitelist(Database db)
    {
        var raw = db.GetSetting("whitelist.patterns");
        if (string.IsNullOrWhiteSpace(raw)) return;
        lock (_gate)
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!_userPatterns.Contains(line, StringComparer.OrdinalIgnoreCase))
                    _userPatterns.Add(line);
        }
    }

    public void SaveUserWhitelist(Database db, IEnumerable<string> patterns)
    {
        lock (_gate)
        {
            _userPatterns.Clear();
            foreach (var p in patterns)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (!_userPatterns.Contains(t, StringComparer.OrdinalIgnoreCase)) _userPatterns.Add(t);
            }
        }
        db.SetSetting("whitelist.patterns", string.Join('\n', UserPatterns));
    }

    /// <summary>是否为 Windows 目录名中的系统保留项。</summary>
    public static bool IsTempArea(string path)
    {
        var lower = PathTools.Key(path).ToLowerInvariant();
        return lower.Contains("\\temp\\") || lower.Contains("\\tmp\\") ||
               lower.Contains("\\inetcache\\") || lower.Contains("\\crashdumps\\") ||
               lower.Contains("\\temporary internet files\\");
    }

    /// <summary>是否被系统默认保护（Windows/Program Files/用户档案/系统卷目录）。</summary>
    public bool IsSystemProtected(string path)
    {
        var key = PathTools.Key(path);
        if (key.Length == 0) return true;

        // 临时目录是唯一例外：它虽然位于用户配置目录之下，但正是需要清理的对象。
        // 若不排除，用户永远无法清空自己的 %TEMP%，与"一键清理安全级文件"的目标直接冲突。
        if (IsTempArea(key)) return false;

        if (_protectedExact.Contains(key)) return true;
        foreach (var prefix in _protectedPrefix)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        // 卷根的系统目录（$Recycle.Bin 等）以及卷根本身不允许整体删除
        var root = PathTools.VolumeRoot(key);
        if (!string.IsNullOrEmpty(root))
        {
            var trimmed = key.Length > 3 ? key[3..] : string.Empty;
            if (trimmed.Length == 0) return true;
            var firstSep = trimmed.IndexOf('\\');
            var leaf = (firstSep < 0 ? trimmed : trimmed[..firstSep]).TrimEnd('\\');
            if (PathTools.IsSystemFolderName(leaf)) return true;
        }

        return false;
    }

    /// <summary>是否命中用户白名单模式。</summary>
    public bool IsUserWhitelisted(string path)
    {
        var key = PathTools.Key(path);
        lock (_gate)
        {
            foreach (var pattern in _userPatterns)
            {
                if (MatchesPattern(key, pattern)) return true;
            }
        }
        return false;
    }

    /// <summary>整体保护判定：系统保护或用户白名单。</summary>
    public bool IsProtected(string path) => IsSystemProtected(path) || IsUserWhitelisted(path);

    /// <summary>通配符匹配（* 与 ?），大小写不敏感，路径分隔符两侧统一。</summary>
    public static bool MatchesPattern(string normalizedPath, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var p = pattern.Trim().Replace('/', '\\');
        var path = normalizedPath.Replace('/', '\\');
        return WildcardMatch(path, p);
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, starIdx = -1, match = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                match = t;
                p++;
            }
            else if (starIdx >= 0)
            {
                p = starIdx + 1;
                match++;
                t = match;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>
    /// 危险扩展名：可执行体、驱动、脚本、注册表、系统配置。
    /// 这类文件即使位于可清理目录也不进入一键清理（避免清理掉便携程序或卸载器）。
    /// </summary>
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".msi", ".msp", ".msu", ".cab",
        ".cpl", ".ocx", ".scr", ".com", ".efi", ".mui", ".manifest",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".reg", ".inf", ".lnk", ".url",
        ".db", ".dat", ".mdb", ".accdb", ".pf", ".evtx", ".etl", ".pol",
    };

    /// <summary>可直接判定为“安全级”的目录特征（大小写不敏感、按路径片段匹配）。</summary>
    private static readonly string[] SafeDirMarkers =
    [
        "\\temp\\", "\\tmp\\", "\\cache\\", "\\caches\\", "\\inetcache\\",
        "\\temporary internet files\\", "\\wtemp\\", "\\crashdumps\\",
        "\\softwaredistribution\\download\\", "\\windows\\temp\\",
        "\\appdata\\local\\temp\\", "\\$recycle.bin\\", "\\prefetch\\",
        "\\logs\\temp\\", "\\thumbnails\\", "\\d3dscache\\",
        "\\google\\chrome\\user data\\default\\cache\\",
        "\\microsoft\\edge\\user data\\default\\cache\\",
        "\\mozilla\\firefox\\profiles\\",
    ];

    /// <summary>可直接判定为“安全级”的文件名特征（临时文件、日志溢出、旧安装包等）。</summary>
    private static readonly string[] SafeFileMarkers =
    [
        "\\thumbs.db", "\\desktop.ini.bak", "\\~$", "\\.tmp", "\\.temp", "\\.bak_old",
        "\\.dmp", "\\.old", "\\.chk", "\\.crdownload", "\\.part", "\\.partial",
        "\\.download", "\\.!ut", "\\.bc!", "\\.tmpfile",
    ];

    /// <summary>旧安装包：位于下载目录或名为 setup/install 的安装包。</summary>
    private static bool LooksLikeOldInstaller(string path, string name)
    {
        var lower = name.ToLowerInvariant();
        if (!(lower.EndsWith(".exe") || lower.EndsWith(".msi") || lower.EndsWith(".zip") ||
              lower.EndsWith(".7z") || lower.EndsWith(".rar") || lower.EndsWith(".iso") ||
              lower.EndsWith(".msix") || lower.EndsWith(".appx"))) return false;
        var dir = Path.GetDirectoryName(path)?.ToLowerInvariant() ?? string.Empty;
        if (dir.EndsWith("\\downloads") || dir.EndsWith("\\下载")) return true;
        return lower.StartsWith("setup") || lower.StartsWith("install") || lower.StartsWith("安装") ||
               lower.Contains("_setup") || lower.Contains("-setup") || lower.Contains("installer");
    }

    public static bool IsDangerousExtension(string ext) => DangerousExtensions.Contains(ext);

    /// <summary>
    /// 计算风险等级。参数 <paramref name="isUserDocumentArea"/> 表示位于用户文档类目录，
    /// 用于把“用户文档目录下的大文件”判为谨慎级。
    /// </summary>
    public RiskLevel EvaluateRisk(string path, string ext, long size, bool isUserDocumentArea)
    {
        var key = PathTools.Key(path);
        if (key.Length == 0) return RiskLevel.Dangerous;

        var lower = key.ToLowerInvariant();
        var name = Path.GetFileName(lower);

        // 安全级判定放在受保护判定之前：临时目录本身位于用户配置目录之下，
        // 若先判"受保护"会把 %TEMP% 误判为危险级，导致临时文件无法清理。
        var isTemp = IsTempArea(key);

        if (!isTemp && IsProtected(path)) return RiskLevel.Dangerous;

        // 安全级：临时目录、缓存目录、回收站、崩溃转储
        foreach (var marker in SafeDirMarkers)
        {
            if (lower.Contains(marker)) return RiskLevel.Safe;
        }

        // 安全级：临时/转储类文件特征
        foreach (var marker in SafeFileMarkers)
        {
            if (marker.StartsWith('\\'))
            {
                if (name == marker[1..]) return RiskLevel.Safe;
            }
            else if (lower.EndsWith(marker)) return RiskLevel.Safe;
        }

        if (name.EndsWith(".log") && (lower.Contains("\\temp") || lower.Contains("\\log"))) return RiskLevel.Safe;

        // 危险级：可执行体与系统配置
        if (IsDangerousExtension(ext)) return RiskLevel.Dangerous;

        // 谨慎级：用户文档目录下的大文件、旧安装包
        if (LooksLikeOldInstaller(path, name)) return RiskLevel.Safe;
        if (isUserDocumentArea && size >= 100L * 1024 * 1024) return RiskLevel.Caution;

        // 其余：用户数据文件，不进入安全清理
        return RiskLevel.Caution;
    }

    /// <summary>是否位于用户文档类目录（桌面/文档/图片/音乐/视频/下载）。</summary>
    public static bool IsUserDocumentArea(string path)
    {
        var key = PathTools.Key(path);
        foreach (var folder in UserDocumentFolders())
        {
            if (key.StartsWith(folder, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IEnumerable<string> UserDocumentFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.UserProfile,
        ];
        foreach (var f in folders)
        {
            string dir;
            try { dir = Environment.GetFolderPath(f); } catch { continue; }
            if (string.IsNullOrEmpty(dir)) continue;
            var key = PathTools.Key(dir);
            yield return key.EndsWith('\\') ? key : key + "\\";
        }

        // 下载目录不是 SpecialFolder 常量，按用户配置目录名拼接
        string profile;
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch { profile = string.Empty; }
        if (!string.IsNullOrEmpty(profile))
        {
            foreach (var name in new[] { "Downloads", "下载" })
            {
                var key = PathTools.Key(Path.Combine(profile, name));
                yield return key.EndsWith('\\') ? key : key + "\\";
            }
        }
    }
}
