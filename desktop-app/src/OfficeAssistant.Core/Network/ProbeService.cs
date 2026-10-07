using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Network;

public enum ProbeStatus
{
    Unknown,
    Reachable,
    Slow,
    Unreachable,
    DnsFailed,
}

/// <summary>单次探测结果。</summary>
public sealed record ProbeResult(
    string Host,
    int Port,
    ProbeStatus Status,
    long DnsMs,
    long TcpMs,
    long HttpMs,
    int HttpStatus,
    string Message)
{
    /// <summary>综合延迟：取 HTTP（若可得）否则 TCP 往返。</summary>
    public long LatencyMs => HttpMs > 0 ? HttpMs : TcpMs > 0 ? TcpMs : DnsMs;

    public string Group { get; init; } = string.Empty;

    /// <summary>
    /// 失败发生在哪一段。
    ///
    /// 这个字段是"能行动"的关键：只说"不可连"没法处理，
    /// 而"DNS 解析失败"（域名被解析污染）、"TCP 握手失败"（连接被重置）、
    /// "HTTPS 握手失败"（TLS 被中断）三者对应的应对方式完全不同。
    /// 三段计时本来就测了，这里只是把它翻译成人能看懂的一行结论。
    /// </summary>
    public string FailureStage => Status switch
    {
        ProbeStatus.Reachable or ProbeStatus.Slow or ProbeStatus.Unknown => string.Empty,
        ProbeStatus.DnsFailed => "DNS 解析失败（域名可能被解析到错误地址）",
        _ when DnsMs > 0 && TcpMs == 0 => "TCP 连接失败（端口连不上，可能被拦截或超时）",
        _ when TcpMs > 0 && HttpMs == 0 => "HTTPS 握手失败（能连上端口但 TLS 被中断）",
        _ => "连接失败",
    };

    /// <summary>该目标对应的处理建议；能直连时为空。</summary>
    public string Advice => Status switch
    {
        ProbeStatus.Reachable or ProbeStatus.Slow or ProbeStatus.Unknown => string.Empty,
        ProbeStatus.DnsFailed => "改用镜像源克隆，或把 DNS 换成公共 DNS 后重试",
        _ when DnsMs > 0 && TcpMs == 0 => "改用镜像源前缀克隆；下载用镜像加速链接",
        _ when TcpMs > 0 && HttpMs == 0 => "改用 SSH over 443，或改用镜像源",
        _ => "改用镜像源",
    };

    public string LatencyText => Status switch
    {
        ProbeStatus.Reachable => $"{LatencyMs} ms",
        ProbeStatus.Slow => $"{LatencyMs} ms（偏慢）",
        ProbeStatus.DnsFailed => "DNS 解析失败",
        ProbeStatus.Unreachable => "不可达",
        _ => "未测",
    };
}

/// <summary>
/// 连通性测速探针（功能一）。
///
/// 合规说明：只做 DNS/TCP/TLS/HTTP 的**被动连通性测量**——
/// 不修改 hosts、不建立隧道、不代理流量、不针对任何受限资源做特殊处理，
/// 测不通的目标只如实展示结果并提示用户走官方渠道。
/// </summary>
public sealed class ProbeService
{
    /// <summary>默认探测目标：GitHub 与 Steam 的常用公开域名。</summary>
    public static readonly (string Host, int Port, string Group, string Note)[] DefaultTargets =
    [
        ("github.com", 443, "GitHub", "代码托管主站"),
        ("api.github.com", 443, "GitHub", "API 接口"),
        ("codeload.github.com", 443, "GitHub", "仓库打包下载（克隆数据源）"),
        ("objects.githubusercontent.com", 443, "GitHub", "Release/ 附件下载"),
        ("raw.githubusercontent.com", 443, "GitHub", "原始文件"),
        ("ssh.github.com", 443, "GitHub", "SSH over 443 端口"),
        ("store.steampowered.com", 443, "Steam", "商店页面"),
        ("steamcommunity.com", 443, "Steam", "社区页面"),
        ("api.steampowered.com", 443, "Steam", "社区 Web API"),
        ("cdn.steamstatic.com", 443, "Steam", "静态资源 CDN"),
        ("steamcdn-a.akamaihd.net", 443, "Steam", "下载 CDN 节点"),
        ("cm.steampowered.com", 443, "Steam", "连接管理器"),
    ];

    public int DnsTimeoutMs { get; set; } = 3000;
    public int TcpTimeoutMs { get; set; } = 4000;
    public int HttpTimeoutMs { get; set; } = 8000;

    /// <summary>超过该延迟视为"偏慢"。</summary>
    public int SlowThresholdMs { get; set; } = 1500;

    public async Task<List<ProbeResult>> ProbeAllAsync(IEnumerable<(string Host, int Port, string Group, string Note)>? targets = null,
        IProgress<ProbeResult>? progress = null, CancellationToken token = default)
    {
        var list = (targets ?? DefaultTargets).ToList();
        var results = new List<ProbeResult>(list.Count);
        var gate = new SemaphoreSlim(6);

        var tasks = list.Select(async target =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await ProbeAsync(target.Host, target.Port, token).ConfigureAwait(false);
                result = result with { Group = target.Group };
                lock (results) results.Add(result);
                progress?.Report(result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderBy(r => r.Group).ThenBy(r => r.Host).ToList();
    }

    /// <summary>对单个目标做 DNS → TCP → HTTPS 三段测量。</summary>
    public async Task<ProbeResult> ProbeAsync(string host, int port, CancellationToken token = default)
    {
        long dnsMs = 0, tcpMs = 0, httpMs = 0;
        var httpStatus = 0;
        string? resolvedIp = null;

        // 1) DNS
        var sw = Stopwatch.StartNew();
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            dnsCts.CancelAfter(DnsTimeoutMs);
            var addresses = await Dns.GetHostAddressesAsync(host, dnsCts.Token).ConfigureAwait(false);
            sw.Stop();
            dnsMs = sw.ElapsedMilliseconds;
            resolvedIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                         ?? addresses.FirstOrDefault()?.ToString();
            if (resolvedIp is null)
                return new ProbeResult(host, port, ProbeStatus.DnsFailed, dnsMs, 0, 0, 0, "DNS 未返回地址");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(host, port, ProbeStatus.DnsFailed, sw.ElapsedMilliseconds, 0, 0, 0, $"DNS 解析失败：{ex.Message}");
        }

        // 2) TCP
        sw.Restart();
        try
        {
            using var client = new TcpClient();
            using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            tcpCts.CancelAfter(TcpTimeoutMs);
            await client.ConnectAsync(resolvedIp, port, tcpCts.Token).ConfigureAwait(false);
            sw.Stop();
            tcpMs = sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(host, port, ProbeStatus.Unreachable, dnsMs, sw.ElapsedMilliseconds, 0, 0,
                $"TCP {port} 连接失败：{ShortError(ex)}");
        }

        // 3) HTTPS 往返（能拿到任意状态码即说明链路通畅）
        if (port == 443)
        {
            sw.Restart();
            var viaProxyFailed = false;
            try
            {
                using var http = CreateHttpClient(useSystemProxy: true);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                sw.Stop();
                httpMs = sw.ElapsedMilliseconds;
                httpStatus = (int)response.StatusCode;
            }
            catch (Exception first)
            {
                sw.Stop();
                var proxyInfo = ReadSystemProxy();
                var proxyInPlay = proxyInfo.AnyConfigured;

                // 关键：HTTPS 失败但 TCP 已通时，用直连再试一次。
                // 系统代理一旦配置了却不可用，走代理的 HTTPS 会超时，
                // 而同一目标直连完全正常——那样报"不可连"是误判，会误导用户以为域名被封。
                try
                {
                    using var direct = CreateHttpClient(useSystemProxy: false);
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
                    using var response = await direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    sw.Stop();
                    httpMs = sw.ElapsedMilliseconds;
                    httpStatus = (int)response.StatusCode;

                    var proxyNote = proxyInPlay
                        ? $"（走系统代理失败、直连成功：{ShortError(first)}；建议检查系统代理设置）"
                        : $"（首次 HTTPS 失败、重试成功：{ShortError(first)}）";
                    var retryStatus = httpMs > SlowThresholdMs ? ProbeStatus.Slow : ProbeStatus.Reachable;
                    return new ProbeResult(host, port, retryStatus, dnsMs, tcpMs, httpMs, httpStatus,
                        "链路正常" + proxyNote);
                }
                catch (Exception second)
                {
                    viaProxyFailed = proxyInPlay;
                    httpMs = 0;
                    var status = tcpMs > SlowThresholdMs ? ProbeStatus.Slow : ProbeStatus.Reachable;
                    var hint = viaProxyFailed
                        ? "HTTPS 未完成（系统代理可能不可用，直连重试也失败）"
                        : "TCP 可达，HTTPS 未完成";
                    return new ProbeResult(host, port, status, dnsMs, tcpMs, 0, 0,
                        $"{hint}：{ShortError(second)}");
                }
            }
        }

        var finalStatus = (httpMs > 0 ? httpMs : tcpMs) > SlowThresholdMs ? ProbeStatus.Slow : ProbeStatus.Reachable;
        var message = finalStatus == ProbeStatus.Slow
            ? "可达但延迟偏高，可尝试切换镜像源或优化配置"
            : "链路正常";
        return new ProbeResult(host, port, finalStatus, dnsMs, tcpMs, httpMs, httpStatus, message);
    }

    private static string ShortError(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "超时",
        SocketException se => se.SocketErrorCode.ToString(),
        _ => ex.Message.Length > 60 ? ex.Message[..60] : ex.Message,
    };

    /// <summary>
    /// 构造探测用的 HTTP 客户端。
    ///
    /// <paramref name="useSystemProxy"/> 为 false 时强制直连：
    /// HTTPS 走系统代理失败后必须能试一次不走代理的请求，否则会把
    /// "代理不可用"误判成"目标域名不可达"。
    /// </summary>
    private static HttpClient CreateHttpClient(bool useSystemProxy)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // 测速只关心链路可达性，证书链问题不影响可达性判定
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = useSystemProxy,
        };
        if (useSystemProxy) handler.Proxy = WebRequest.DefaultWebProxy;

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(8000) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OfficeAssistant-Probe/1.0");
        return client;
    }

    /// <summary>读取系统代理设置（WinHTTP + 环境变量），供"代理设置引导"展示。</summary>
    public static SystemProxyInfo ReadSystemProxy()
    {
        var info = new SystemProxyInfo
        {
            EnvironmentHttpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY")
                                   ?? Environment.GetEnvironmentVariable("http_proxy"),
            EnvironmentHttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                                    ?? Environment.GetEnvironmentVariable("https_proxy"),
            EnvironmentNoProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                                 ?? Environment.GetEnvironmentVariable("no_proxy"),
        };

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is not null)
            {
                var enable = key.GetValue("ProxyEnable");
                info.InternetSettingsEnabled = enable is int i && i != 0;
                info.InternetSettingsProxy = key.GetValue("ProxyServer") as string;
                info.AutoConfigUrl = key.GetValue("AutoConfigURL") as string;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 Internet Settings 代理配置失败：{ex.Message}");
        }

        try
        {
            info.WinHttpProxy = ReadWinHttpProxy();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 WinHTTP 代理配置失败：{ex.Message}");
        }

        return info;
    }

    private static string? ReadWinHttpProxy()
    {
        // 通过 netsh 读取当前用户的 WinHTTP 代理（只读查询，不做任何写入）
        try
        {
            var psi = new ProcessStartInfo("netsh", "winhttp show proxy")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains("代理服务器", StringComparison.Ordinal) || t.Contains("Proxy Server", StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class SystemProxyInfo
{
    public bool InternetSettingsEnabled { get; set; }
    public string? InternetSettingsProxy { get; set; }
    public string? AutoConfigUrl { get; set; }
    public string? WinHttpProxy { get; set; }
    public string? EnvironmentHttpProxy { get; set; }
    public string? EnvironmentHttpsProxy { get; set; }
    public string? EnvironmentNoProxy { get; set; }

    public bool AnyConfigured =>
        InternetSettingsEnabled || !string.IsNullOrWhiteSpace(AutoConfigUrl) ||
        !string.IsNullOrWhiteSpace(EnvironmentHttpProxy) || !string.IsNullOrWhiteSpace(EnvironmentHttpsProxy);

    public string Describe()
    {
        var parts = new List<string>();
        if (InternetSettingsEnabled)
            parts.Add($"系统代理（Internet 选项）：{InternetSettingsProxy ?? "已启用"}");
        if (!string.IsNullOrWhiteSpace(AutoConfigUrl))
            parts.Add($"自动配置脚本：{AutoConfigUrl}");
        if (!string.IsNullOrWhiteSpace(EnvironmentHttpProxy))
            parts.Add($"环境变量 HTTP_PROXY={EnvironmentHttpProxy}");
        if (!string.IsNullOrWhiteSpace(EnvironmentHttpsProxy))
            parts.Add($"环境变量 HTTPS_PROXY={EnvironmentHttpsProxy}");
        if (!string.IsNullOrWhiteSpace(WinHttpProxy))
            parts.Add($"WinHTTP：{WinHttpProxy}");
        return parts.Count == 0 ? "未检测到系统级代理配置。" : string.Join("；", parts);
    }
}
