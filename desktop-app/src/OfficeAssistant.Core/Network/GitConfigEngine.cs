using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Network;

public sealed record CommandOutput(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Text => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : $"{StdOut.Trim()}\n{StdErr.Trim()}".Trim();
}

/// <summary>配置写入前的快照（用于一键还原）。</summary>
public sealed class ConfigSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Description { get; set; } = string.Empty;

    /// <summary>git 全局配置：键 → 值（未设置的不包含）。</summary>
    public Dictionary<string, string> GitConfig { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>~/.ssh/config 的原始内容；null 表示当时文件不存在。</summary>
    public string? SshConfig { get; set; }

    /// <summary>进程级/用户级代理环境变量的原值。</summary>
    public Dictionary<string, string?> EnvironmentProxy { get; set; } = new();
}

/// <summary>
/// Git 配置优化与回滚引擎（功能一）。
///
/// 所有写入前先落一份快照（git 全局配置 + ~/.ssh/config + 代理环境变量），
/// 一键还原时按快照逐项恢复，未设置的键会被 unset，因此不会留下残留配置。
/// 明确不做的事：不修改 hosts、不注册系统代理服务、不安装任何网络组件。
/// </summary>
public sealed class GitConfigEngine
{
    private const string GitTimeoutMs = "20000";
    private readonly Database _db;

    public GitConfigEngine(Database db)
    {
        _db = db;
        SnapshotDirectory = AppPaths.GitSnapshotDirectory;
        Directory.CreateDirectory(SnapshotDirectory);
    }

    public string SnapshotDirectory { get; }

    /// <summary>探测 git 可执行文件路径；未安装返回 null。</summary>
    public string? FindGit()
    {
        foreach (var candidate in CandidateGitPaths())
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略
            }
        }

        // PATH 兜底
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), "git.exe");
                if (File.Exists(full)) return full;
            }
            catch
            {
                // 忽略
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateGitPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
            yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
        }
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Git", "cmd", "git.exe");
        }
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
            yield return Path.Combine(localAppData, "GitHubDesktop", "app-", "git", "cmd", "git.exe");
        }
    }

    public string? GitPath { get; private set; }

    public string? GitVersion { get; private set; }

    public bool GitAvailable { get; private set; }

    /// <summary>检测 git 环境。</summary>
    public bool DetectGit()
    {
        GitPath = FindGit();
        GitAvailable = GitPath is not null;
        GitVersion = null;
        if (!GitAvailable) return false;

        var result = RunGit(["--version"]);
        if (result.Ok) GitVersion = result.Text.Trim();
        return true;
    }

    public CommandOutput RunGit(IReadOnlyList<string> arguments, string? workingDirectory = null, int timeoutMs = 20000)
        => RunProcess(GitPath ?? "git", arguments, workingDirectory, timeoutMs);

    /// <summary>通用进程执行（不做 shell 解析，参数逐个传递，避免注入）。</summary>
    public static CommandOutput RunProcess(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null,
        int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new CommandOutput(-1, string.Empty, "无法启动进程");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { /* 忽略 */ }
                return new CommandOutput(-1, string.Empty, $"执行超时（{timeoutMs} ms）");
            }

            return new CommandOutput(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new CommandOutput(-1, string.Empty, ex.Message);
        }
    }

    // ------------------------------------------------------------------ 读取与快照

    /// <summary>读取 git 全局配置为字典。</summary>
    public Dictionary<string, string> ReadGlobalConfig()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!GitAvailable) return map;

        var result = RunGit(["config", "--global", "--list"]);
        if (!result.Ok) return map;

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    public static string SshConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public ConfigSnapshot CreateSnapshot(string description)
    {
        var snapshot = new ConfigSnapshot
        {
            Description = description,
            GitConfig = ReadGlobalConfig(),
            SshConfig = ReadSshConfig(),
        };
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy" })
            snapshot.EnvironmentProxy[name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

        SaveSnapshot(snapshot);
        Log.Info($"已创建配置快照 {snapshot.Id}：{description}，含 {snapshot.GitConfig.Count} 项 git 配置");
        return snapshot;
    }

    private static string? ReadSshConfig()
    {
        try
        {
            return File.Exists(SshConfigPath) ? File.ReadAllText(SshConfigPath, Encoding.UTF8) : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveSnapshot(ConfigSnapshot snapshot)
    {
        try
        {
            var file = Path.Combine(SnapshotDirectory, $"{snapshot.CreatedAt:yyyyMMdd-HHmmss}-{snapshot.Id}.json");
            var json = JsonSerializer.Serialize(snapshot, SnapshotJsonContext.Default.ConfigSnapshot);
            File.WriteAllText(file, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置快照失败：{ex.Message}");
        }
    }

    public List<ConfigSnapshot> ListSnapshots()
    {
        var list = new List<ConfigSnapshot>();
        try
        {
            foreach (var file in Directory.GetFiles(SnapshotDirectory, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var snapshot = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ConfigSnapshot);
                    if (snapshot is not null) list.Add(snapshot);
                }
                catch
                {
                    // 跳过损坏的快照文件
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取快照列表失败：{ex.Message}");
        }
        return list;
    }

    // ------------------------------------------------------------------ 应用配置

    public sealed record ApplyResult(bool Success, string Message, List<string> Details, string? SnapshotId);

    /// <summary>
    /// 应用 Git 配置优化。只处理"用户勾选"的项，并在写入前自动创建快照。
    /// </summary>
    public ApplyResult ApplyGitConfig(IReadOnlyList<(string Key, string Value)> items, string description)
    {
        if (!GitAvailable)
            return new ApplyResult(false, "未检测到 Git，请先安装 Git for Windows。", [], null);
        if (items.Count == 0)
            return new ApplyResult(false, "没有勾选任何配置项。", [], null);

        var snapshot = CreateSnapshot(description);
        var details = new List<string>();
        var failed = 0;

        foreach (var (key, value) in items)
        {
            var result = RunGit(["config", "--global", key, value]);
            if (result.Ok)
            {
                details.Add($"✓ {key} = {value}");
                Log.Audit("应用 Git 配置", key, value, "成功");
            }
            else
            {
                failed++;
                details.Add($"✗ {key} = {value} —— {result.Text}");
                Log.Audit("应用 Git 配置", key, value, "失败", result.Text);
            }
        }

        _db.LogOperation(new Models.OperationLogEntry
        {
            Operation = "应用 Git 配置",
            Source = $"{items.Count} 项",
            Result = failed == 0 ? "成功" : $"部分失败（{failed} 项）",
            Detail = description,
        });

        return failed == 0
            ? new ApplyResult(true, $"已应用 {items.Count} 项配置，快照 {snapshot.Id} 可用于一键还原。", details, snapshot.Id)
            : new ApplyResult(false, $"{items.Count - failed}/{items.Count} 项成功，快照 {snapshot.Id}。", details, snapshot.Id);
    }

    /// <summary>写入用户级代理环境变量（含 git http.proxy），并保留原值以便还原。</summary>
    public ApplyResult ApplyProxy(string proxyUrl, bool alsoSetGitProxy, bool alsoSetEnvironment)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return new ApplyResult(false, "代理地址不能为空。", [], null);
        if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "socks5"))
        {
            return new ApplyResult(false, "代理地址格式无效，应形如 http://127.0.0.1:7890。", [], null);
        }

        var snapshot = CreateSnapshot("应用用户自备代理");
        var details = new List<string>();
        var failed = 0;

        if (alsoSetGitProxy && GitAvailable)
        {
            foreach (var key in new[] { "http.proxy", "https.proxy" })
            {
                var result = RunGit(["config", "--global", key, proxyUrl]);
                if (result.Ok) details.Add($"✓ git {key} = {proxyUrl}");
                else { failed++; details.Add($"✗ git {key} —— {result.Text}"); }
            }
        }

        if (alsoSetEnvironment)
        {
            try
            {
                Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl, EnvironmentVariableTarget.User);
                details.Add($"✓ 用户环境变量 HTTP_PROXY / HTTPS_PROXY = {proxyUrl}");
            }
            catch (Exception ex)
            {
                failed++;
                details.Add($"✗ 写入环境变量失败：{ex.Message}");
            }
        }

        _db.LogOperation(new Models.OperationLogEntry
        {
            Operation = "应用代理配置",
            Source = proxyUrl,
            Result = failed == 0 ? "成功" : "部分失败",
            Detail = $"git={alsoSetGitProxy}, env={alsoSetEnvironment}",
        });
        Log.Audit("应用代理配置", proxyUrl, null, failed == 0 ? "成功" : "部分失败");

        details.Add("提示：代理由用户自行提供与维护，本工具不内置、不启动任何代理或隧道组件；" +
                    "还原时按快照恢复原值。");

        return new ApplyResult(failed == 0, failed == 0 ? "代理配置已写入。" : "部分写入失败。", details, snapshot.Id);
    }

    // ------------------------------------------------------------------ 一键还原

    /// <summary>按快照还原配置（git 全局配置 + ~/.ssh/config + 代理环境变量）。</summary>
    public ApplyResult RestoreSnapshot(ConfigSnapshot snapshot)
    {
        var details = new List<string>();
        var failed = 0;

        if (GitAvailable)
        {
            var current = ReadGlobalConfig();
            foreach (var key in current.Keys.Union(snapshot.GitConfig.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (snapshot.GitConfig.TryGetValue(key, out var original))
                {
                    if (current.TryGetValue(key, out var now) && now == original) continue;
                    var result = RunGit(["config", "--global", key, original]);
                    if (result.Ok) details.Add($"↺ {key} = {original}");
                    else { failed++; details.Add($"✗ 还原 {key} 失败：{result.Text}"); }
                }
                else if (current.ContainsKey(key))
                {
                    var result = RunGit(["config", "--global", "--unset", key]);
                    if (result.Ok || result.ExitCode == 5) details.Add($"↺ 移除 {key}（快照中不存在）");
                    else { failed++; details.Add($"✗ 移除 {key} 失败：{result.Text}"); }
                }
            }
        }

        // ~/.ssh/config：快照为 null 表示当时不存在，则删除我们新增的文件；否则写回原内容
        try
        {
            if (snapshot.SshConfig is null)
            {
                if (File.Exists(SshConfigPath))
                {
                    File.Delete(SshConfigPath);
                    details.Add("↺ 已删除本工具新增的 ~/.ssh/config");
                }
            }
            else
            {
                File.WriteAllText(SshConfigPath, snapshot.SshConfig, new UTF8Encoding(false));
                details.Add("↺ 已还原 ~/.ssh/config");
            }
        }
        catch (Exception ex)
        {
            failed++;
            details.Add($"✗ 还原 ~/.ssh/config 失败：{ex.Message}");
        }

        foreach (var (name, original) in snapshot.EnvironmentProxy)
        {
            try
            {
                var current = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (string.Equals(current, original, StringComparison.Ordinal)) continue;
                Environment.SetEnvironmentVariable(name, original, EnvironmentVariableTarget.User);
                details.Add(original is null ? $"↺ 已清除环境变量 {name}" : $"↺ {name} = {original}");
            }
            catch (Exception ex)
            {
                failed++;
                details.Add($"✗ 还原 {name} 失败：{ex.Message}");
            }
        }

        _db.LogOperation(new Models.OperationLogEntry
        {
            Operation = "一键还原配置",
            Source = snapshot.Id,
            Result = failed == 0 ? "成功" : "部分失败",
            Detail = snapshot.Description,
        });
        Log.Audit("一键还原配置", snapshot.Id, null, failed == 0 ? "成功" : "部分失败", snapshot.Description);

        return new ApplyResult(failed == 0,
            failed == 0 ? $"已按快照 {snapshot.Id} 完成还原。" : $"还原完成但有 {failed} 项失败。",
            details, snapshot.Id);
    }

    /// <summary>写出 SSH over 443 配置（追加到 ~/.ssh/config，写入前已由调用方创建快照）。</summary>
    public ApplyResult WriteSshOver443()
    {
        try
        {
            var snapshot = CreateSnapshot("写入 SSH over 443 配置");
            var dir = Path.GetDirectoryName(SshConfigPath)!;
            Directory.CreateDirectory(dir);

            var existing = File.Exists(SshConfigPath) ? File.ReadAllText(SshConfigPath, Encoding.UTF8) : string.Empty;
            if (existing.Contains("HostName ssh.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return new ApplyResult(true, "~/.ssh/config 中已存在 GitHub over 443 配置，未重复写入。", [], snapshot.Id);
            }

            var content = existing.TrimEnd() + Environment.NewLine + Environment.NewLine + MirrorCatalog.BuildSshOver443Config() + Environment.NewLine;
            File.WriteAllText(SshConfigPath, content, new UTF8Encoding(false));

            _db.LogOperation(new Models.OperationLogEntry
            {
                Operation = "写入 SSH over 443 配置",
                Source = SshConfigPath,
                Result = "成功",
            });
            Log.Audit("写入 SSH over 443 配置", SshConfigPath, null, "成功");

            return new ApplyResult(true, $"已写入 {SshConfigPath}，可用一键还原恢复。",
                [$"✓ 追加 Host github.com → ssh.github.com:443"], snapshot.Id);
        }
        catch (Exception ex)
        {
            Log.Error("写入 SSH 配置失败", ex);
            return new ApplyResult(false, $"写入失败：{ex.Message}", [], null);
        }
    }

    /// <summary>克隆加速对比测量：同一 URL 分别用官方源与镜像前缀计时（只测连接与首包，不落盘）。</summary>
    public async Task<string> MeasureCloneAsync(string repositoryUrl, string? mirrorPrefix, CancellationToken token)
    {
        var lines = new List<string>();
        if (!GitAvailable) return "未检测到 Git，无法测量。";

        var mirrorUrl = string.IsNullOrWhiteSpace(mirrorPrefix)
            ? repositoryUrl
            : new MirrorEntry("临时", mirrorPrefix!, "", "").Rewrite(repositoryUrl);

        var official = await MeasureOneAsync(repositoryUrl, token).ConfigureAwait(false);
        lines.Add($"官方源  {repositoryUrl}\n    {official}");

        if (!string.Equals(mirrorUrl, repositoryUrl, StringComparison.OrdinalIgnoreCase))
        {
            var mirror = await MeasureOneAsync(mirrorUrl, token).ConfigureAwait(false);
            lines.Add($"镜像源  {mirrorUrl}\n    {mirror}");
        }

        return string.Join("\n", lines);
    }

    private async Task<string> MeasureOneAsync(string url, CancellationToken token)
    {
        // git ls-remote 只读取引用列表，不下载对象，是衡量"克隆握手+首个响应"最轻量的方式
        var stopwatch = Stopwatch.StartNew();
        var result = await Task.Run(() => RunGit(["ls-remote", "--heads", url], null, 25000), token).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.Ok)
        {
            var refs = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            return $"成功，{stopwatch.ElapsedMilliseconds} ms，读取到 {refs} 个分支引用";
        }
        return $"失败，{stopwatch.ElapsedMilliseconds} ms —— {result.Text}";
    }
}

/// <summary>JSON 源生成上下文。</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(ConfigSnapshot))]
internal partial class SnapshotJsonContext : JsonSerializerContext
{
}
