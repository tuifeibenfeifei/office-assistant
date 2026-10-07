namespace OfficeAssistant.Core.Utils;

/// <summary>
/// 应用数据路径。全部数据仅存本地用户目录，不上传；卸载或“清除本地数据”时直接删除该目录即可。
/// 默认位置：%LOCALAPPDATA%\全能办公助手
///
/// 目录选择采用**多候选 + 可写探测**：某个位置不可写时自动降级到下一个，
/// 而不是让程序整体启动失败。这一点很关键——企业策略、安全软件、漫游配置或
/// 权限残缺的用户配置目录都可能让默认位置不可写，但用户仍然应该能用这个工具。
/// </summary>
public static class AppPaths
{
    public const string ProductName = "全能办公助手";

    public static string DataRoot { get; private set; } = string.Empty;

    /// <summary>标记文件：放在程序目录下即启用便携模式（数据写在程序目录旁的 data 子目录）。</summary>
    public const string PortableFlagName = "portable.flag";

    /// <summary>本次启动实际尝试过的候选目录与结果，供启动取证与失败报告使用。</summary>
    public static IReadOnlyList<string> ResolutionAttempts => Attempts;

    private static readonly List<string> Attempts = [];

    /// <summary>数据目录是否已成功确定（false 表示所有候选都不可写）。</summary>
    public static bool IsReady => DataRoot.Length > 0;

    public static string DatabaseFile => Path.Combine(DataRoot, "officeassistant.db");
    public static string LogDirectory => Path.Combine(DataRoot, "logs");
    public static string AuditLogFile => Path.Combine(LogDirectory, "operations.log");
    public static string RuntimeLogFile => Path.Combine(LogDirectory, "runtime.log");
    public static string ConfigDirectory => Path.Combine(DataRoot, "config");
    public static string GitSnapshotDirectory => Path.Combine(ConfigDirectory, "git-snapshots");
    public static string CredentialFile => Path.Combine(ConfigDirectory, "credential.dat");
    public static string CacheDirectory => Path.Combine(DataRoot, "cache");

    /// <summary>
    /// 确定数据目录并创建必需的子目录。
    /// 候选顺序：显式参数 → 环境变量 → 便携模式 → %LOCALAPPDATA% → %APPDATA% → 程序目录。
    /// 每个候选都会用一次真实的写入探测验证可写性；全部失败时抛 <see cref="IOException"/>，
    /// 由调用方转成用户可读的启动失败报告。
    /// </summary>
    public static void Initialize(string? overrideRoot = null)
    {
        Attempts.Clear();

        foreach (var (candidate, source) in EnumerateCandidates(overrideRoot))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                Attempts.Add($"[跳过] {source}：路径为空");
                continue;
            }

            if (TryPrepare(candidate, out var error))
            {
                DataRoot = candidate;
                Attempts.Add($"[成功] {source}：{candidate}");
                return;
            }

            Attempts.Add($"[失败] {source}：{candidate} —— {error}");
        }

        DataRoot = string.Empty;
        throw new IOException(
            "所有候选数据目录都无法写入，程序无法启动。尝试记录：\n  " +
            string.Join("\n  ", Attempts) +
            "\n\n解决办法：\n" +
            $"  1) 在程序目录下新建一个空文件 {PortableFlagName}，改用便携模式（数据写在程序目录旁的 data 子目录）；\n" +
            "  2) 或设置环境变量 OFFICEASSISTANT_DATA_DIR 指向一个可写目录；\n" +
            "  3) 或检查安全软件 / 组策略是否阻止了程序在用户配置目录下创建文件。");
    }

    private static IEnumerable<(string Path, string Source)> EnumerateCandidates(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            yield return (overrideRoot, "显式指定");

        var fromEnvironment = Environment.GetEnvironmentVariable("OFFICEASSISTANT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            yield return (fromEnvironment, "环境变量 OFFICEASSISTANT_DATA_DIR");

        if (PortableRoot() is { } portable)
            yield return (Path.Combine(portable, "data"), $"便携模式（{PortableFlagName}）");

        // 标准位置：本地应用数据 → 漫游应用数据 → 程序目录旁
        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
            yield return (Path.Combine(localAppData, ProductName), "%LOCALAPPDATA%");

        var roamingAppData = SafeFolder(Environment.SpecialFolder.ApplicationData);
        if (roamingAppData.Length > 0)
            yield return (Path.Combine(roamingAppData, ProductName), "%APPDATA%");

        var exeDir = SafeBaseDirectory();
        if (exeDir.Length > 0)
            yield return (Path.Combine(exeDir, "data"), "程序目录旁的 data 子目录");

        // 最后兜底：临时目录（数据会在系统清理时丢失，但至少能用起来）
        var temp = SafeTempPath();
        if (temp.Length > 0)
            yield return (Path.Combine(temp, ProductName), "%TEMP%");
    }

    /// <summary>创建目录并做一次真实写入探测——只判断"目录存在"不足以说明可写。</summary>
    private static bool TryPrepare(string root, out string error)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            Directory.CreateDirectory(Path.Combine(root, "config", "git-snapshots"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));

            // 真实写一次：某些位置允许建目录但拒绝写文件（例如受策略保护的目录）
            var probe = Path.Combine(root, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeBaseDirectory()
    {
        try { return AppContext.BaseDirectory ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeTempPath()
    {
        try { return Path.GetTempPath() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string? PortableRoot()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            return exeDir.Length > 0 && File.Exists(Path.Combine(exeDir, PortableFlagName)) ? exeDir : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除本地数据（保留正在运行的日志文件句柄，故日志目录尽力删除）。</summary>
    public static void ClearAllData(bool includeDatabase = true)
    {
        TryDeleteDirectory(ConfigDirectory);
        TryDeleteDirectory(CacheDirectory);
        if (includeDatabase)
        {
            TryDeleteFile(DatabaseFile);
            TryDeleteFile(DatabaseFile + "-wal");
            TryDeleteFile(DatabaseFile + "-shm");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* 尽力而为 */ }
    }
}
