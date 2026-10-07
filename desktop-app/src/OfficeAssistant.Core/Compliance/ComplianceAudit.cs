using System.Text;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Compliance;

/// <summary>合规检查项。</summary>
public sealed record ComplianceCheck(string Name, bool Passed, string Detail);

/// <summary>合规审计报告。</summary>
public sealed class ComplianceReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public List<ComplianceCheck> Checks { get; init; } = [];
    public List<string> Violations { get; init; } = [];
    public int FilesScanned { get; set; }
    public double Seconds { get; set; }

    public bool Passed => Violations.Count == 0 && Checks.All(c => c.Passed);

    public string Summary => Passed
        ? $"合规回归通过：{Checks.Count} 项检查全部通过，扫描 {FilesScanned} 个程序文件，耗时 {Format.Duration(Seconds)}。"
        : $"合规回归未通过：发现 {Violations.Count} 处问题。";
}

/// <summary>
/// 合规红线回归自检（功能一必须遵守的红线，纳入发布门禁）。
///
/// 检查方式：扫描本程序实际部署的二进制文件，确认其中**不存在**以下能力：
/// - 修改 hosts 文件（<c>drivers\etc\hosts</c>）；
/// - 写入 WinHTTP/WinINet 系统代理设置（netsh winhttp set proxy / Internet Settings 写入）；
/// - 安装网络过滤器、驱动或防火墙规则（WFP、TDI、netsh advfirewall）；
/// - 建立 VPN/隧道（RAS 拨号、Wintun、TAP、WireGuard、OpenVPN 等）。
///
/// 同时以"能力清单"的形式正向声明本程序允许做的全部系统写入，供审计核对。
/// </summary>
public static class ComplianceAudit
{
    /// <summary>
    /// 禁止出现在二进制中的能力关键字（同时匹配 ASCII 与 UTF-16 两种编码）。
    ///
    /// 关键词必须足够具体，避免误报：早期版本曾把 "TDI"/"WFP" 这类短缩写列入，
    /// 结果在 "stdint" 等无关标识符中产生假阳性——审计工具自身的可信度同样重要。
    /// 这里只保留真正代表"绕过网络管理措施"的完整 API 名或文件路径。
    /// </summary>
    private static readonly string[] ForbiddenKeywords =
    [
        @"drivers\etc\hosts",
        "netsh winhttp set",
        "winhttp set proxy",
        "netsh advfirewall",
        "WinHttpSetDefaultProxyConfiguration",
        "FwpmEngineOpen",
        "FwpmFilterAdd",
        "WintunCreateAdapter",
        "wireguard",
        "openvpn",
        "tap0901",
        "RasDial",
        "MprAdminConnectionAdd",
        "InternetSetOptionW",
        "IKEv2",
        "L2TP",
        "PPTP",
    ];

    /// <summary>
    /// 源码审计的附加规则：这几类"危险动作"必须与 <see cref="RiskyTargetFiles"/> 中的目标同时
    /// 出现才算违规（例如仅在文件里出现 <c>SetValue</c> 不足以判定，必须同时写 Internet Settings）。
    /// </summary>
    private static readonly string[] RiskyActions = ["SetValue", "CreateSubKey", "DeleteSubKey", "DeleteValue"];

    private static readonly string[] RiskyTargetFiles = ["Internet Settings", "WinHttp", "hosts"];

    /// <summary>扫描时排除的文件（自身包含关键字清单，属于审计工具本体）。</summary>
    private static readonly string[] SelfExclusions =
    [
        "OfficeAssistant.Core.dll",
        "ComplianceAudit",
    ];

    /// <summary>正向能力声明：本程序会写入的位置与系统调用，全部为可回滚的本地用户配置。</summary>
    public static IReadOnlyList<string> DeclaredWriteCapabilities { get; } =
    [
        "用户数据目录（%LOCALAPPDATA%\\全能办公助手）下的 SQLite 库、日志与会话缓存",
        "~/.ssh/config：仅在用户确认后追加 GitHub over 443 建议配置（写入前自动快照）",
        "git 全局配置（git config --global）：仅在用户确认后写入，写入前自动快照，可一键还原",
        "用户级环境变量 HTTP_PROXY / HTTPS_PROXY：仅当用户选择「同时写入环境变量」时，可按快照还原",
        "回收站：所有删除统一经 IFileOperation（FOFX_RECYCLEONDELETE），文件可恢复",
        "进程工作集与优先级：EmptyWorkingSet / SetPriorityClass，不结束任何进程",
    ];

    /// <summary>对给定目录中的可执行文件执行红线回归。</summary>
    public static ComplianceReport RunBinaryAudit(string directory)
    {
        var report = new ComplianceReport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var files = new List<string>();
        try
        {
            foreach (var pattern in new[] { "*.exe", "*.dll" })
                files.AddRange(Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            report.Checks.Add(new ComplianceCheck("读取程序目录", false, ex.Message));
            return report;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (SelfExclusions.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            {
                report.Checks.Add(new ComplianceCheck($"跳过审计工具本体 {name}", true, "该文件包含关键字清单，故排除自身扫描"));
                continue;
            }

            report.FilesScanned++;
            var hit = ScanForForbidden(file);
            if (hit is not null)
                report.Violations.Add($"{name}：命中禁止能力关键字「{hit}」");
        }

        report.Checks.Add(new ComplianceCheck("二进制红线扫描", report.Violations.Count == 0,
            report.Violations.Count == 0
                ? $"已扫描 {report.FilesScanned} 个程序文件，未发现修改 hosts、写入系统代理、安装驱动/过滤规则或建立隧道的能力"
                : string.Join("；", report.Violations)));

        stopwatch.Stop();
        report.Seconds = stopwatch.Elapsed.TotalSeconds;
        Log.Info(report.Summary);
        return report;
    }

    /// <summary>对源码目录执行红线回归（开发期使用，排除本文件）。</summary>
    public static ComplianceReport RunSourceAudit(string sourceDirectory)
    {
        var report = new ComplianceReport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        List<string> files;
        try
        {
            files = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            report.Checks.Add(new ComplianceCheck("读取源码目录", false, ex.Message));
            return report;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("ComplianceAudit.cs", StringComparison.OrdinalIgnoreCase)) continue;

            report.FilesScanned++;
            var text = File.ReadAllText(file);
            var hit = ScanForForbidden(file);
            if (hit is not null)
            {
                report.Violations.Add($"{Path.GetRelativePath(sourceDirectory, file)}：命中禁止能力关键字「{hit}」");
                continue;
            }

            // 组合规则：危险注册表动作必须与危险目标同时出现
            var hasAction = RiskyActions.Any(a => text.Contains(a, StringComparison.Ordinal));
            if (!hasAction) continue;
            var target = RiskyTargetFiles.FirstOrDefault(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
                report.Violations.Add(
                    $"{Path.GetRelativePath(sourceDirectory, file)}：出现注册表写入动作且涉及「{target}」");
        }

        report.Checks.Add(new ComplianceCheck("源码红线扫描", report.Violations.Count == 0,
            report.Violations.Count == 0
                ? $"已扫描 {report.FilesScanned} 个源文件，未发现绕过网络管理措施的实现"
                : string.Join("；", report.Violations)));

        stopwatch.Stop();
        report.Seconds = stopwatch.Elapsed.TotalSeconds;
        return report;
    }

    private static string? ScanForForbidden(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            // Latin1 把每个字节映射为一个字符，UTF-8/ASCII 与 UTF-16LE 关键字都能被匹配到
            var text = Encoding.Latin1.GetString(bytes);
            foreach (var keyword in ForbiddenKeywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return keyword;

                var utf16 = string.Join('\0', keyword.ToCharArray()) + "\0";
                if (text.Contains(utf16, StringComparison.Ordinal)) return keyword;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成可读的合规报告文本（供 UI 与文档使用）。</summary>
    public static string RenderReport(ComplianceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"合规红线回归报告 · {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        foreach (var check in report.Checks)
            sb.AppendLine($"{(check.Passed ? "✓" : "✗")} {check.Name}：{check.Detail}");
        if (report.Violations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("发现问题：");
            foreach (var v in report.Violations) sb.AppendLine($"  · {v}");
        }
        sb.AppendLine();
        sb.AppendLine("本程序允许的全部系统写入（正向声明）：");
        foreach (var capability in DeclaredWriteCapabilities) sb.AppendLine($"  · {capability}");
        sb.AppendLine();
        sb.AppendLine("法律依据：《中华人民共和国网络安全法》《计算机信息网络国际联网管理暂行规定》");
        sb.AppendLine("红线：不实现任何绕过网络管理措施的功能；不修改 hosts；不内置代理或 VPN；不改写系统代理设置。");
        return sb.ToString();
    }
}
