using System.Text;
using OfficeAssistant.Core.Models;

namespace OfficeAssistant.Core.Cleanup;

/// <summary>
/// 清理规则库：默认规则 + 用户自定义规则。
///
/// 风险分级严格遵循《功能说明》：
/// - 安全级：临时文件、缓存、回收站、旧安装包、浏览器缓存 —— 只有这一级进入一键清理；
/// - 谨慎级：用户文档目录下的大文件、重复文件 —— 只提示，需用户单独决策；
/// - 危险级：系统目录、Program Files、运行中占用文件 —— 永不进入清单，且删除时被硬拦截。
/// </summary>
public static class CleanupRules
{
    /// <summary>默认规则（首次运行时写入 cleanup_rules 表，用户可关闭但不可把危险项改成安全项）。</summary>
    public static IReadOnlyList<CleanupRule> Defaults { get; } =
    [
        // ---- 临时文件与缓存（安全级）
        new() { Pattern = @"*\AppData\Local\Temp\*", Category = "临时文件", Risk = RiskLevel.Safe, Note = "用户临时目录" },
        new() { Pattern = @"*\Windows\Temp\*", Category = "临时文件", Risk = RiskLevel.Safe, Note = "系统临时目录" },
        new() { Pattern = @"*\Temp\*.tmp", Category = "临时文件", Risk = RiskLevel.Safe, Note = "临时文件" },
        new() { Pattern = @"*\Temp\*.temp", Category = "临时文件", Risk = RiskLevel.Safe, Note = "临时文件" },
        new() { Pattern = @"*\Temp\*.log", Category = "临时文件", Risk = RiskLevel.Safe, Note = "临时日志" },
        new() { Pattern = @"*\Temp\*.dmp", Category = "崩溃转储", Risk = RiskLevel.Safe, Note = "崩溃转储文件" },
        new() { Pattern = @"*\CrashDumps\*", Category = "崩溃转储", Risk = RiskLevel.Safe, Note = "应用崩溃转储" },
        new() { Pattern = @"*\Windows\Prefetch\*.pf", Category = "预读缓存", Risk = RiskLevel.Safe, Note = "预读文件（可重建）" },
        new() { Pattern = @"*\Windows\SoftwareDistribution\Download\*", Category = "更新缓存", Risk = RiskLevel.Safe, Note = "Windows 更新下载缓存" },
        new() { Pattern = @"*\Logs\*.log", Category = "日志", Risk = RiskLevel.Safe, Note = "应用日志" },
        new() { Pattern = @"*\Logs\*.etl", Category = "日志", Risk = RiskLevel.Safe, Note = "跟踪日志" },
        new() { Pattern = @"*\D3DSCache\*", Category = "着色器缓存", Risk = RiskLevel.Safe, Note = "DirectX 着色器缓存" },
        new() { Pattern = @"*\NVIDIA\DXCache\*", Category = "着色器缓存", Risk = RiskLevel.Safe, Note = "NVIDIA 着色器缓存" },
        new() { Pattern = @"*\NVIDIA\GLCache\*", Category = "着色器缓存", Risk = RiskLevel.Safe, Note = "NVIDIA OpenGL 缓存" },
        new() { Pattern = @"*\Microsoft\Windows\INetCache\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "IE/系统网络缓存" },
        new() { Pattern = @"*\Microsoft\Windows\Temporary Internet Files\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "临时网络文件" },
        new() { Pattern = @"*\Microsoft\Windows\Explorer\thumbcache_*.db", Category = "缩略图缓存", Risk = RiskLevel.Safe, Note = "缩略图缓存（可重建）" },
        new() { Pattern = @"*\Google\Chrome\User Data\*\Cache\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "Chrome 缓存" },
        new() { Pattern = @"*\Google\Chrome\User Data\*\Code Cache\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "Chrome 代码缓存" },
        new() { Pattern = @"*\Microsoft\Edge\User Data\*\Cache\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "Edge 缓存" },
        new() { Pattern = @"*\Mozilla\Firefox\Profiles\*\cache2\*", Category = "浏览器缓存", Risk = RiskLevel.Safe, Note = "Firefox 缓存" },
        new() { Pattern = @"*\pip\cache\*", Category = "开发缓存", Risk = RiskLevel.Safe, Note = "pip 下载缓存" },
        new() { Pattern = @"*\npm-cache\*", Category = "开发缓存", Risk = RiskLevel.Safe, Note = "npm 下载缓存" },
        new() { Pattern = @"*\.gradle\caches\*", Category = "开发缓存", Risk = RiskLevel.Safe, Note = "Gradle 缓存" },
        new() { Pattern = @"*\.nuget\packages\*", Category = "开发缓存", Risk = RiskLevel.Safe, Note = "NuGet 包缓存" },
        new() { Pattern = @"*\Crashpad\*", Category = "崩溃转储", Risk = RiskLevel.Safe, Note = "Crashpad 报告" },
        new() { Pattern = @"*\WER\ReportArchive\*", Category = "错误报告", Risk = RiskLevel.Safe, Note = "Windows 错误报告" },
        new() { Pattern = @"*\WER\ReportQueue\*", Category = "错误报告", Risk = RiskLevel.Safe, Note = "Windows 错误报告队列" },

        // ---- 旧安装包与下载残留（安全级，仅限下载目录）
        new() { Pattern = @"*\Downloads\*.msi", Category = "旧安装包", Risk = RiskLevel.Safe, Note = "下载目录中的安装包" },
        new() { Pattern = @"*\Downloads\*.msu", Category = "旧安装包", Risk = RiskLevel.Safe, Note = "系统更新包" },
        new() { Pattern = @"*\Downloads\*.crdownload", Category = "下载残留", Risk = RiskLevel.Safe, Note = "未完成的下载" },
        new() { Pattern = @"*\Downloads\*.part", Category = "下载残留", Risk = RiskLevel.Safe, Note = "未完成的下载" },
        new() { Pattern = @"*\Downloads\*.partial", Category = "下载残留", Risk = RiskLevel.Safe, Note = "未完成的下载" },
        new() { Pattern = @"*\Downloads\*.!ut", Category = "下载残留", Risk = RiskLevel.Safe, Note = "未完成的下载" },
        new() { Pattern = @"*\下载\*.crdownload", Category = "下载残留", Risk = RiskLevel.Safe, Note = "未完成的下载" },

        // ---- 谨慎级：只提示，不进入一键清理
        new() { Pattern = @"*\Downloads\*.zip", Category = "下载归档", Risk = RiskLevel.Caution, Note = "下载目录中的压缩包，请自行确认" },
        new() { Pattern = @"*\Downloads\*.iso", Category = "下载归档", Risk = RiskLevel.Caution, Note = "系统镜像，体积大但不建议自动清理" },
        new() { Pattern = @"*\Downloads\*.exe", Category = "下载程序", Risk = RiskLevel.Caution, Note = "安装程序，请自行确认" },
    ];

    /// <summary>把 seed 规则写入数据库（已存在的模式不覆盖用户改动）。</summary>
    public static void Seed(Data.Database db)
    {
        try
        {
            var existing = db.GetRules().Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in Defaults)
            {
                if (existing.Contains(rule.Pattern)) continue;
                db.UpsertRule(rule);
            }
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"写入默认清理规则失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把通配符模式转换成 SQL LIKE 表达式。
    /// 转换规则：<c>*</c> → <c>%</c>，<c>?</c> → <c>_</c>，
    /// 路径分隔符 <c>\</c> 是 LIKE 的转义符，故双写；同时转义用户模式里已有的 <c>%</c> 与 <c>_</c>。
    /// </summary>
    public static string ToLikePattern(string wildcard)
    {
        var sb = new StringBuilder(wildcard.Length * 2 + 2);
        sb.Append('\\');   // 匹配路径开头（例如 D:\...）
        foreach (var ch in wildcard)
        {
            switch (ch)
            {
                case '*': sb.Append('%'); break;
                case '?': sb.Append('_'); break;
                case '\\': sb.Append("\\\\"); break;
                case '%': sb.Append("\\%"); break;
                case '_': sb.Append("\\_"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
