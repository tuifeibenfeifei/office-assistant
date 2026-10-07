using System.Text;

namespace OfficeAssistant.Core.Utils;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 分级日志（debug/info/warn/error）写入本地文件；默认不自动上报。
/// 操作审计日志（operations.log）与运行日志（runtime.log）分离：
/// 审计日志只由安全引擎追加，且不提供 UI 删除入口（对应《项目计划书》三·（六））。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static long _runtimeBytes;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex)
        => Write(LogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                File.AppendAllText(AppPaths.RuntimeLogFile, line + Environment.NewLine, Encoding.UTF8);
                _runtimeBytes += line.Length + 1;
                if (_runtimeBytes > 4L * 1024 * 1024) RollRuntimeLog();
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    /// <summary>操作审计（不可由普通功能删除）。</summary>
    public static void Audit(string operation, string? source, string? destination, string result, string? detail = null)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\t');
        sb.Append(operation).Append('\t');
        sb.Append(source ?? "-").Append('\t');
        sb.Append(destination ?? "-").Append('\t');
        sb.Append(result);
        if (!string.IsNullOrEmpty(detail)) sb.Append('\t').Append(detail.Replace('\n', ' ').Replace('\r', ' '));
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                File.AppendAllText(AppPaths.AuditLogFile, sb + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static void RollRuntimeLog()
    {
        try
        {
            var archive = Path.Combine(AppPaths.LogDirectory,
                $"runtime-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (File.Exists(AppPaths.RuntimeLogFile)) File.Move(AppPaths.RuntimeLogFile, archive, true);
            _runtimeBytes = 0;
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取最近 N 行运行日志，供 UI 日志面板展示。</summary>
    public static string Tail(int lines = 300)
    {
        try
        {
            if (!File.Exists(AppPaths.RuntimeLogFile)) return "(暂无运行日志)";
            var all = File.ReadAllLines(AppPaths.RuntimeLogFile);
            var take = Math.Min(lines, all.Length);
            return string.Join(Environment.NewLine, all.Skip(all.Length - take));
        }
        catch (Exception ex)
        {
            return $"(读取日志失败：{ex.Message})";
        }
    }
}
