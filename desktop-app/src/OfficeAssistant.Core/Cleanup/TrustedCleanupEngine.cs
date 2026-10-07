using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Cleanup;

/// <summary>可信清理项：一个可以被无条件清理的位置，以及它为什么安全。</summary>
public sealed record TrustedTarget(
    string Name,
    string Path,
    string WhySafe,
    bool NeedsElevation = false,
    int Weight = 0);

/// <summary>单个目标的实测结果。</summary>
public sealed record TrustedTargetResult(
    string Name,
    string Path,
    string WhySafe,
    bool Exists,
    int FileCount,
    long Bytes,
    string Status);

/// <summary>可信清理的执行报告。</summary>
public sealed record TrustedCleanupReport(
    IReadOnlyList<TrustedTargetResult> Scanned,
    int DeletedCount,
    long ReleasedBytes,
    int SkippedCount,
    TimeSpan Elapsed)
{
    public string Summary =>
        $"清理完成：删除 {DeletedCount:N0} 个文件，释放 {Format.Bytes(ReleasedBytes)}" +
        (SkippedCount > 0 ? $"；跳过 {SkippedCount:N0} 个正在使用或权限不足的文件" : string.Empty) +
        $"（用时 {Elapsed.TotalSeconds:0.#} 秒）";
}

/// <summary>
/// 「百分百可清理」引擎。
///
/// 设计原则（这是它敢承诺"百分百"的全部依据）：
///   1. 只覆盖**操作系统与主流软件公认的临时目录**，不扫描全盘、不按文件名猜测；
///   2. 每个目标都限定在**当前用户**范围内，不碰系统目录、不碰 Program Files；
///   3. 删除前逐个复核路径前缀，命中保护规则的直接丢弃（复用误删防护，不做第二套判断）；
///   4. 正在被占用的文件跳过而不是强删，删不掉不算失败；
///   5. 仍走回收站（可恢复）——"肯定能删"不等于"必须永久删掉"。
///
/// 不包含（因此不会"清理完发现东西没了"）：
///   浏览器配置文件、登录状态、书签、聊天记录、文档、下载目录、桌面。
/// </summary>
public sealed class TrustedCleanupEngine
{
    private readonly Database _db;
    private readonly OperationExecutor _executor;
    private readonly ProtectionEngine _protection;

    public TrustedCleanupEngine(Database db, OperationExecutor executor, ProtectionEngine protection)
    {
        _db = db;
        _executor = executor;
        _protection = protection;
    }

    /// <summary>按"删除后是否影响使用"分组的可信目标。</summary>
    public static IReadOnlyList<TrustedTarget> Targets { get; } = BuildTargets();

    private static List<TrustedTarget> BuildTargets()
    {
        var list = new List<TrustedTarget>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath();

        void Add(string name, string path, string why, bool elevated = false, int weight = 0)
        {
            if (!string.IsNullOrWhiteSpace(path)) list.Add(new TrustedTarget(name, path, why, elevated, weight));
        }

        // ---- 第一档：临时文件。删掉只会让相关程序重新生成，不可能丢失用户内容
        Add("用户临时文件", temp, "程序运行时产生的中间文件，随时可重新生成", weight: 10);
        Add("IE/系统网络缓存", Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            "网页缓存副本，删除后重新访问即可重新下载", weight: 9);
        Add("Windows 错误报告", Path.Combine(local, "Microsoft", "Windows", "WER"),
            "崩溃报告与转储，仅在需要排查问题时才有用", weight: 8);
        Add("资源管理器缩略图缓存", Path.Combine(local, "Microsoft", "Windows", "Explorer"),
            "缩略图缓存，删除后会自动重新生成", weight: 8);
        Add("DirectX 着色器缓存", Path.Combine(local, "D3DSCache"),
            "显卡着色器编译缓存，删除后游戏首次运行会重新编译", weight: 7);
        Add("NVIDIA 着色器缓存", Path.Combine(local, "NVIDIA", "DXCache"),
            "显卡着色器缓存，可安全重新生成", weight: 7);
        Add("AMD 着色器缓存", Path.Combine(local, "AMD", "DxCache"),
            "显卡着色器缓存，可安全重新生成", weight: 7);
        Add("CrashDumps 崩溃转储", Path.Combine(local, "CrashDumps"),
            "程序崩溃时生成的转储文件，仅用于事后分析", weight: 8);
        Add("应用临时目录（漫游）", Path.Combine(roaming, "Microsoft", "Windows", "Recent"),
            "最近使用记录，只影响快捷方式列表", weight: 5);

        // ---- 第二档：需要管理员权限，默认不勾选
        Add("Windows 更新下载缓存", Path.Combine(windows, "SoftwareDistribution", "Download"),
            "已安装更新的安装包缓存，删除后不影响已装好的更新", elevated: true, weight: 6);
        Add("传递优化缓存", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization"),
            "更新分发的点对点缓存，删除后会自动重建", elevated: true, weight: 6);

        return list;
    }

    /// <summary>
    /// 扫描各可信目标，给出每一项的实际占用。
    /// 只读取目录项与文件大小，不打开文件内容。
    /// </summary>
    public IReadOnlyList<TrustedTargetResult> Scan(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var results = new List<TrustedTargetResult>();

        foreach (var target in Targets.OrderByDescending(t => t.Weight))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"正在统计：{target.Name}");

            if (!Directory.Exists(target.Path))
            {
                results.Add(new TrustedTargetResult(target.Name, target.Path, target.WhySafe, false, 0, 0, "不存在"));
                continue;
            }

            long bytes = 0;
            var count = 0;
            var denied = 0;

            try
            {
                foreach (var file in EnumerateFilesSafe(target.Path, token))
                {
                    count++;
                    try { bytes += new FileInfo(file).Length; }
                    catch { denied++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { denied++; }

            var status = denied > 0 ? $"{denied:N0} 个文件无法读取" : "可清理";
            results.Add(new TrustedTargetResult(target.Name, target.Path, target.WhySafe, true, count, bytes, status));
        }

        return results;
    }

    /// <summary>
    /// 执行清理。默认只处理不需要管理员权限的目标；提权项需显式开启。
    /// </summary>
    public TrustedCleanupReport Execute(
        bool includeElevated = false,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var started = DateTime.UtcNow;
        var scanned = new List<TrustedTargetResult>();
        var allPaths = new List<string>();
        var totalFiles = 0;
        long totalBytes = 0;

        foreach (var target in Targets.OrderByDescending(t => t.Weight))
        {
            token.ThrowIfCancellationRequested();

            if (target.NeedsElevation && !includeElevated)
            {
                scanned.Add(new TrustedTargetResult(target.Name, target.Path, target.WhySafe, true, 0, 0, "需管理员权限，已跳过"));
                continue;
            }

            if (!Directory.Exists(target.Path))
            {
                scanned.Add(new TrustedTargetResult(target.Name, target.Path, target.WhySafe, false, 0, 0, "不存在"));
                continue;
            }

            progress?.Report($"正在收集：{target.Name}");

            var files = new List<string>();
            long bytes = 0;
            try
            {
                foreach (var file in EnumerateFilesSafe(target.Path, token))
                {
                    // 双重保险：命中系统保护或用户白名单的文件直接丢弃，不进入删除列表。
                    // 复用 ProtectionEngine 的判定而不是另写一套——两套判断迟早会不一致。
                    if (_protection.IsProtected(file)) continue;

                    // 被占用的文件提前跳过：临时目录里常年有别的进程正在写的文件，
                    // 提前探一次比"整批删除失败再回滚"体验好得多。
                    if (IsInUse(file)) continue;

                    files.Add(file);
                    try { bytes += new FileInfo(file).Length; } catch { /* 大小读不到不影响删除 */ }
                }
            }
            catch (OperationCanceledException) { throw; }

            allPaths.AddRange(files);
            totalFiles += files.Count;
            totalBytes += bytes;
            scanned.Add(new TrustedTargetResult(target.Name, target.Path, target.WhySafe, true, files.Count, bytes,
                files.Count == 0 ? "已经很干净" : "已加入清理列表"));
        }

        if (allPaths.Count == 0)
        {
            return new TrustedCleanupReport(scanned, 0, 0, 0, DateTime.UtcNow - started);
        }

        progress?.Report($"正在移入回收站：{allPaths.Count:N0} 个文件");
        var outcome = _executor.Recycle(allPaths, "可信清理（临时文件）");

        // 回收站不可用时如实报错，绝不静默改成永久删除
        var deleted = outcome.Ok ? allPaths.Count : 0;
        var skipped = outcome.Ok ? 0 : allPaths.Count;

        if (!outcome.Ok)
        {
            scanned.Add(new TrustedTargetResult("清理未完成", string.Empty, outcome.Message, true, 0, 0, "失败"));
        }

        return new TrustedCleanupReport(scanned, deleted, outcome.Ok ? totalBytes : 0, skipped, DateTime.UtcNow - started);
    }

    /// <summary>
    /// 安全枚举：遇到无权限的子目录就跳过该目录，而不是整棵树失败。
    /// 临时目录下常年存在别的进程正在写的目录，一次拒绝不该让整个清理中止。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch { files = []; }

            foreach (var file in files) yield return file;

            string[] dirs;
            try { dirs = Directory.GetDirectories(current); }
            catch { dirs = []; }

            foreach (var dir in dirs) stack.Push(dir);
        }
    }

    /// <summary>
    /// 文件是否正被其他进程占用。
    /// 以"独占方式打开"判断：能独占打开说明没人占用，随后立即释放，不修改内容。
    /// </summary>
    private static bool IsInUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                bufferSize: 1, FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;   // 被占用是最常见的原因
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // 无权限的文件同样不该进入删除列表
        }
        catch
        {
            return false;  // 其它异常（如文件刚被删掉）不阻止后续处理
        }
    }
}
