using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Cleanup;

public sealed record CleanupExecutionReport(
    int Requested,
    int Deleted,
    long FreedBytes,
    int Blocked,
    int Failed,
    double Seconds,
    IReadOnlyList<string> Messages)
{
    public string Summary =>
        $"清理完成：删除 {Deleted}/{Requested} 项，释放 {Format.Bytes(FreedBytes)}，耗时 {Format.Duration(Seconds)}" +
        (Blocked > 0 ? $"，被误删防护拦截 {Blocked} 项" : string.Empty) +
        (Failed > 0 ? $"，失败 {Failed} 项" : string.Empty);
}

/// <summary>
/// 一键清理引擎（功能三）。
///
/// 只对"可安全删除"清单执行：先预览并估算释放空间，用户确认后执行；
/// 删除统一走回收站 API（可恢复），并逐条写入操作日志与审计日志。
/// 谨慎级与危险级永远不进入清单 —— 这是误删防护的硬约束，而非 UI 提示。
/// </summary>
public sealed class CleanupEngine
{
    private readonly Database _db;
    private readonly ProtectionEngine _protection;
    private readonly OperationExecutor _executor;

    public CleanupEngine(Database db, ProtectionEngine protection, OperationExecutor executor)
    {
        _db = db;
        _protection = protection;
        _executor = executor;
    }

    public sealed class CleanupPlan
    {
        public List<CleanupCandidate> Items { get; init; } = [];
        public long TotalBytes { get; set; }
        public long SelectedBytes { get; set; }
        public int SelectedCount { get; set; }
        public double ScanSeconds { get; set; }
        public List<string> Warnings { get; init; } = [];

        public string EstimateText =>
            $"共 {Items.Count} 项，合计 {Format.Bytes(TotalBytes)}；已选 {SelectedCount} 项，预计释放 {Format.Bytes(SelectedBytes)}";
    }

    /// <summary>构建一键清理预演清单（只包含安全级）。</summary>
    public CleanupPlan BuildPlan(Action<string>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = new CleanupPlan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = _db.GetRules().Where(r => r.Enabled && r.Risk == RiskLevel.Safe).ToList();

        progress?.Invoke($"正在匹配 {rules.Count} 条安全级清理规则…");

        foreach (var rule in rules)
        {
            List<FileEntry> matches;
            try
            {
                matches = _db.MatchRule(CleanupRules.ToLikePattern(rule.Pattern), 4000);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"规则 {rule.Pattern} 匹配失败：{ex.Message}");
                continue;
            }

            foreach (var entry in matches)
            {
                if (!seen.Add(entry.Path)) continue;

                // 双保险：即使数据库里的 risk 字段是旧值，也用当前防护引擎重新判定一次
                var risk = _protection.EvaluateRisk(entry.Path, entry.Ext, entry.Size,
                    ProtectionEngine.IsUserDocumentArea(entry.Path));
                if (risk != RiskLevel.Safe) continue;
                if (_protection.IsProtected(entry.Path)) continue;

                plan.Items.Add(new CleanupCandidate
                {
                    Path = entry.Path,
                    Name = entry.Name,
                    IsDirectory = false,
                    Size = entry.Size,
                    Risk = RiskLevel.Safe,
                    Category = rule.Category,
                    Rule = rule.Pattern,
                    Action = PlanAction.Delete,
                    Selected = true,
                });
            }
        }

        plan.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        plan.TotalBytes = plan.Items.Sum(i => i.Size);
        plan.SelectedCount = plan.Items.Count;
        plan.SelectedBytes = plan.TotalBytes;
        stopwatch.Stop();
        plan.ScanSeconds = stopwatch.Elapsed.TotalSeconds;

        if (plan.Items.Count == 0)
            plan.Warnings.Add("未发现可安全清理的文件。若尚未扫描过磁盘，请先在「磁盘管理」页执行一次扫描。");

        var protectedCount = rules.Count;
        Log.Info($"清理预演完成：{protectedCount} 条规则，候选 {plan.Items.Count} 项，{Format.Bytes(plan.TotalBytes)}");
        return plan;
    }

    /// <summary>按用户勾选执行清理。</summary>
    public CleanupExecutionReport Execute(IReadOnlyList<CleanupCandidate> selections, Action<string>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        var targets = selections
            .Where(s => s is { Selected: true, Action: PlanAction.Delete })
            .Select(s => s.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
            return new CleanupExecutionReport(0, 0, 0, 0, 0, 0, ["没有勾选任何条目"]);

        progress?.Invoke($"正在把 {targets.Count} 个条目移入回收站…");
        long before = 0;
        foreach (var candidate in selections.Where(s => s.Selected && s.Action == PlanAction.Delete))
            before += candidate.Size;

        var result = _executor.Recycle(targets, "一键清理");
        stopwatch.Stop();

        var deleted = result.Status == OperationStatus.Success ? result.AffectedCount : 0;
        var freed = result.Status == OperationStatus.Success ? before : 0;
        var blocked = result.Status == OperationStatus.Blocked ? targets.Count : 0;
        var failed = result.Status is OperationStatus.Failed or OperationStatus.Cancelled ? targets.Count : 0;

        messages.Add(result.Message);
        progress?.Invoke(result.Message);

        Log.Info($"一键清理：{result.Status}，{result.Message}");
        return new CleanupExecutionReport(targets.Count, deleted, freed, blocked, failed,
            stopwatch.Elapsed.TotalSeconds, messages);
    }

    // ------------------------------------------------------------------ 重复文件（谨慎级）

    public sealed record DuplicateGroup(long Size, string Hash, List<FileEntry> Files);

    /// <summary>
    /// 查找重复文件：先按大小分组缩小范围，再对组内文件计算 MD5 确认。
    /// 结果属于"谨慎级"，只提示不自动删除；保留每组最早修改的一份。
    /// </summary>
    public List<DuplicateGroup> FindDuplicates(long minBytes, int maxGroups, IProgress<string>? progress, CancellationToken token)
    {
        var result = new List<DuplicateGroup>();
        var candidates = _db.DuplicateCandidates(minBytes, maxGroups);
        progress?.Report($"按大小筛出 {candidates.Count} 组候选，正在计算 MD5 校验…");

        foreach (var (size, files) in candidates)
        {
            if (token.IsCancellationRequested) break;
            var byHash = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (token.IsCancellationRequested) break;
                var hash = TryComputeMd5(file.Path);
                if (hash is null) continue;
                file.Md5 = hash;
                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = [];
                    byHash[hash] = list;
                }
                list.Add(file);
            }

            foreach (var (hash, list) in byHash)
            {
                if (list.Count < 2) continue;
                list.Sort((a, b) => a.Mtime.CompareTo(b.Mtime));
                result.Add(new DuplicateGroup(size, hash, list));
            }
        }

        progress?.Report($"发现 {result.Count} 组重复文件，可释放约 {Format.Bytes(result.Sum(g => g.Size * (g.Files.Count - 1)))}");
        return result;
    }

    private static string? TryComputeMd5(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>并发版重复文件扫描（大磁盘更快）。</summary>
    public List<DuplicateGroup> FindDuplicatesParallel(long minBytes, int maxGroups, IProgress<string>? progress, CancellationToken token)
    {
        var groups = _db.DuplicateCandidates(minBytes, maxGroups);
        progress?.Report($"按大小筛出 {groups.Count} 组候选，正在并行计算 MD5…");
        var result = new ConcurrentBag<DuplicateGroup>();

        Parallel.ForEach(groups, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            },
            group =>
            {
                try
                {
                    var byHash = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in group.Files)
                    {
                        var hash = TryComputeMd5(file.Path);
                        if (hash is null) continue;
                        file.Md5 = hash;
                        if (!byHash.TryGetValue(hash, out var list))
                        {
                            list = [];
                            byHash[hash] = list;
                        }
                        list.Add(file);
                    }
                    foreach (var (hash, list) in byHash)
                    {
                        if (list.Count < 2) continue;
                        list.Sort((a, b) => a.Mtime.CompareTo(b.Mtime));
                        result.Add(new DuplicateGroup(group.Size, hash, list));
                    }
                }
                catch
                {
                    // 单组失败不影响整体
                }
            });

        var ordered = result.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList();
        progress?.Report($"发现 {ordered.Count} 组重复文件，可释放约 {Format.Bytes(ordered.Sum(g => g.Size * (g.Files.Count - 1)))}");
        return ordered;
    }
}
