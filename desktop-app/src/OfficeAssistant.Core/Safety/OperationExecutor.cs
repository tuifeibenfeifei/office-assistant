using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Safety;

public enum OperationStatus
{
    Success,
    Conflict,
    Blocked,
    Failed,
    Cancelled,
}

/// <summary>文件操作结果（含审计信息）。</summary>
public sealed record FileOperationResult(
    OperationStatus Status,
    string Message,
    string? Path = null,
    long AffectedBytes = 0,
    int AffectedCount = 0)
{
    public bool Ok => Status == OperationStatus.Success;

    public static FileOperationResult Failed(string message, string? path = null)
        => new(OperationStatus.Failed, message, path);

    public static FileOperationResult Blocked(string message, string? path = null)
        => new(OperationStatus.Blocked, message, path);

    public static FileOperationResult Succeeded(string message, string? path = null, long bytes = 0, int count = 0)
        => new(OperationStatus.Success, message, path, bytes, count);
}

/// <summary>
/// 文件操作执行器：所有移动/删除/重命名都经此收口，保证
/// 「白名单拦截 → 回收站 API → 写操作日志」这条链路不会被绕过。
/// </summary>
public sealed class OperationExecutor
{
    private readonly Database _db;
    private readonly ProtectionEngine _protection;
    private readonly ShellFileOperations _shell = new();

    public OperationExecutor(Database db, ProtectionEngine protection)
    {
        _db = db;
        _protection = protection;
    }

    public ProtectionEngine Protection => _protection;

    public bool UsingModernRecycleApi => _shell.UsingModernApi;

    /// <summary>预检：该路径是否允许删除（返回 null 表示允许，否则返回拦截原因）。</summary>
    public string? CheckDeletable(string path)
    {
        var normalized = PathTools.Normalize(path);
        if (normalized.Length == 0) return "路径无效";
        if (PathTools.IsRoot(normalized)) return "不允许删除整个卷";
        if (!File.Exists(normalized) && !Directory.Exists(normalized)) return "目标不存在";
        if (_protection.IsUserWhitelisted(normalized)) return "命中用户白名单，已保护";
        if (_protection.IsSystemProtected(normalized)) return "系统目录或程序目录，已保护";
        return null;
    }

    /// <summary>删除到回收站（可恢复）。</summary>
    public FileOperationResult Recycle(IReadOnlyList<string> paths, string operation = "删除到回收站")
    {
        if (paths.Count == 0) return new FileOperationResult(OperationStatus.Failed, "没有需要删除的条目");

        var blocked = new List<string>();
        var allowed = new List<string>();
        foreach (var p in paths)
        {
            var reason = CheckDeletable(p);
            if (reason is null) allowed.Add(PathTools.Normalize(p));
            else blocked.Add($"{PathTools.Shorten(p)}（{reason}）");
        }

        foreach (var b in blocked)
        {
            var path = b.Split('（')[0];
            _db.LogOperation(new OperationLogEntry
            {
                Operation = operation,
                Source = path,
                Result = "已拦截",
                Detail = b,
            });
            Log.Audit(operation, path, null, "已拦截", b);
        }

        if (allowed.Count == 0)
            return new FileOperationResult(OperationStatus.Blocked,
                $"全部 {blocked.Count} 个条目均被误删防护拦截。首个原因：{blocked[0]}");

        long bytes = 0;
        var count = 0;
        foreach (var p in allowed)
        {
            var (size, items) = Measure(p);
            bytes += size;
            count += items;
        }

        var result = _shell.Recycle(allowed);
        if (!result.Success && !result.Aborted && blocked.Count == 0)
        {
            // 回收站不可用或权限不足：不静默降级为永久删除，交由用户决定
            _db.LogOperation(new OperationLogEntry
            {
                Operation = operation,
                Source = allowed.Count == 1 ? allowed[0] : $"{allowed.Count} 个条目",
                Result = "失败",
                Detail = result.Message,
            });
            Log.Audit(operation, allowed.Count == 1 ? allowed[0] : $"{allowed.Count} 个条目", null, "失败", result.Message);
            return new FileOperationResult(OperationStatus.Failed, result.Message);
        }

        var status = result.Success ? OperationStatus.Success
            : result.Aborted ? OperationStatus.Cancelled
            : OperationStatus.Failed;

        foreach (var p in allowed)
        {
            _db.LogOperation(new OperationLogEntry
            {
                Operation = operation,
                Source = p,
                Result = status == OperationStatus.Success ? "成功" : result.Message,
                Detail = blocked.Count > 0 ? $"同批被拦截 {blocked.Count} 项" : null,
            });
            Log.Audit(operation, p, "回收站", status == OperationStatus.Success ? "成功" : result.Message);
        }

        var summary = blocked.Count > 0
            ? $"{result.Message}；另有 {blocked.Count} 个条目被误删防护拦截"
            : result.Message;

        return new FileOperationResult(status, summary,
            null,
            status == OperationStatus.Success ? bytes : 0,
            status == OperationStatus.Success ? count : 0);
    }

    /// <summary>移动文件/目录。</summary>
    public FileOperationResult Move(IReadOnlyList<string> sources, string targetDirectory, bool overwrite = false)
    {
        if (sources.Count == 0) return new FileOperationResult(OperationStatus.Failed, "没有需要移动的条目");

        var target = PathTools.Normalize(targetDirectory);
        if (!Directory.Exists(target))
            return new FileOperationResult(OperationStatus.Failed, $"目标目录不存在：{target}");

        foreach (var srcRaw in sources)
        {
            var src = PathTools.Normalize(srcRaw);
            if (string.IsNullOrEmpty(src))
                return new FileOperationResult(OperationStatus.Failed, "源路径无效", srcRaw);

            if (string.Equals(PathTools.Key(src), PathTools.Key(target), StringComparison.Ordinal))
                return new FileOperationResult(OperationStatus.Failed, "源与目标相同，无需移动", src);

            if (PathTools.Key(target).StartsWith(PathTools.Key(src) + "\\", StringComparison.Ordinal))
                return new FileOperationResult(OperationStatus.Failed,
                    $"不能把目录移动到它自己的子目录：{PathTools.Shorten(src)}", src);

            // 同卷根目录之间移动允许；跨卷由 Shell 处理（复制+删除语义由 Shell 保证）
            var name = Path.GetFileName(src);
            var candidate = Path.Combine(target, name);
            if (PathTools.Key(candidate) != PathTools.Key(src) &&
                (File.Exists(candidate) || Directory.Exists(candidate)))
            {
                if (!overwrite)
                    return new FileOperationResult(OperationStatus.Conflict,
                        $"目标目录中已存在同名项：{name}。请选择「跳过」或「覆盖」。", candidate);

                var removed = Recycle([candidate], "移动前覆盖");
                if (!removed.Ok && removed.Status != OperationStatus.Success)
                {
                    return new FileOperationResult(OperationStatus.Failed,
                        $"无法覆盖已存在的同名项：{removed.Message}", candidate);
                }
            }
        }

        var result = _shell.Move(sources, target);
        var status = result.Success ? OperationStatus.Success
            : result.Aborted ? OperationStatus.Cancelled
            : OperationStatus.Failed;

        long bytes = 0;
        var count = 0;
        foreach (var s in sources)
        {
            var (size, items) = Measure(s);
            bytes += size;
            count += items;
        }

        foreach (var s in sources)
        {
            _db.LogOperation(new OperationLogEntry
            {
                Operation = "移动",
                Source = s,
                Destination = target,
                Result = status == OperationStatus.Success ? "成功" : result.Message,
            });
            Log.Audit("移动", s, target, status == OperationStatus.Success ? "成功" : result.Message);
        }

        return new FileOperationResult(status, result.Message,
            null,
            status == OperationStatus.Success ? bytes : 0,
            status == OperationStatus.Success ? count : 0);
    }

    /// <summary>重命名。</summary>
    public FileOperationResult Rename(string path, string newName)
    {
        var src = PathTools.Normalize(path);
        var reason = CheckDeletable(src);
        if (reason is not null && reason != "目标不存在")
            return new FileOperationResult(OperationStatus.Blocked, $"不允许重命名：{reason}", src);

        var result = _shell.Rename(src, newName);
        var status = result.Success ? OperationStatus.Success : OperationStatus.Failed;
        var dst = Path.Combine(Path.GetDirectoryName(src) ?? string.Empty, PathTools.SanitizeFileName(newName));

        _db.LogOperation(new OperationLogEntry
        {
            Operation = "重命名",
            Source = src,
            Destination = dst,
            Result = result.Success ? "成功" : result.Message,
        });
        Log.Audit("重命名", src, dst, result.Success ? "成功" : result.Message);

        return new FileOperationResult(status, result.Message, src);
    }

    private static (long Bytes, int Items) Measure(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return (info.Length, 1);
            }

            if (!Directory.Exists(path)) return (0, 0);
            long total = 0;
            var count = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                count++;
                try { total += new FileInfo(file).Length; } catch { /* 忽略单个文件失败 */ }
            }
            return (total, count);
        }
        catch
        {
            return (0, 0);
        }
    }
}
