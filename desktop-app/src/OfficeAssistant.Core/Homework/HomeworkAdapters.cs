using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeAssistant.Core.Models;
// 模型类 Homework 与命名空间 OfficeAssistant.Core.Homework 同名，这里用别名消除歧义
using HomeworkEntry = OfficeAssistant.Core.Models.Homework;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Homework;

/// <summary>
/// 学习通凭据保险箱：账号密码经 Windows DPAPI 加密后落盘（%LOCALAPPDATA%\全能办公助手\config）。
///
/// 安全约定：明文仅在内存中短暂存在；加密绑定当前 Windows 用户账户，
/// 拷走文件在别的账户/机器上无法解密；提供"立即清除凭据"操作。
/// </summary>
public sealed class CredentialVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OfficeAssistant.Xuexitong.v1");

    public bool HasCredential => File.Exists(AppPaths.CredentialFile);

    public DateTime? SavedAt { get; private set; }

    public string? SavedAccount { get; private set; }

    public bool Save(Credential credential)
    {
        try
        {
            var json = JsonSerializer.Serialize(credential, HomeworkJsonContext.Default.Credential);
            var cipher = Dpapi.Protect(Encoding.UTF8.GetBytes(json), "全能办公助手-学习通凭据");
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            File.WriteAllBytes(AppPaths.CredentialFile, cipher);
            SavedAt = credential.SavedAt;
            SavedAccount = credential.Account;
            Log.Info($"学习通凭据已加密保存（账户 {Mask(credential.Account)}）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("保存学习通凭据失败", ex);
            return false;
        }
    }

    public Credential? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.CredentialFile)) return null;
            var cipher = File.ReadAllBytes(AppPaths.CredentialFile);
            var plain = Dpapi.Unprotect(cipher);
            var json = Encoding.UTF8.GetString(plain);
            var credential = JsonSerializer.Deserialize(json, HomeworkJsonContext.Default.Credential);
            if (credential is not null)
            {
                SavedAt = credential.SavedAt;
                SavedAccount = credential.Account;
            }
            return credential;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取学习通凭据失败（可能需要重新登录）：{ex.Message}");
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.CredentialFile)) File.Delete(AppPaths.CredentialFile);
            SavedAt = null;
            SavedAccount = null;
            Log.Audit("清除学习通凭据", AppPaths.CredentialFile, null, "成功");
        }
        catch (Exception ex)
        {
            Log.Error("清除学习通凭据失败", ex);
        }
    }

    /// <summary>账号脱敏展示（仅保留前 3 位与后 2 位）。</summary>
    public static string Mask(string account)
    {
        if (string.IsNullOrEmpty(account)) return "—";
        if (account.Length <= 5) return account[..1] + new string('*', Math.Max(1, account.Length - 1));
        return account[..3] + new string('*', Math.Max(1, account.Length - 5)) + account[^2..];
    }
}

/// <summary>同步结果（供 UI 更新看板与状态）。</summary>
public sealed record SyncOutcome(
    SyncResult Result,
    string Message,
    List<Course> Courses,
    List<HomeworkEntry> Homework,
    long DurationMs,
    int HttpRequests);

/// <summary>
/// 学习通接口适配器抽象。平台接口一旦变更，只需替换适配器实现，上层（同步引擎、看板、提醒）不受影响。
/// 对应《功能说明》功能二"接口层必须抽象隔离，平台变更时只改适配器"。
/// </summary>
public interface IHomeworkPlatformAdapter
{
    string Name { get; }

    /// <summary>是否已具备真实平台访问能力（未配置端点时为 false，同步将降级为本地模式）。</summary>
    bool IsConfigured { get; }

    /// <summary>能力说明，用于向用户如实交代当前处于哪种模式。</summary>
    string CapabilityDescription { get; }

    Task<SyncOutcome> SyncAsync(Credential? credential, CancellationToken token);
}

/// <summary>
/// 本地/导入模式适配器：不访问任何网络，直接读取用户导入的作业计划。
///
/// 降级路径（对应《功能说明》功能二"若平台禁止自动抓取，降级为手动导出/浏览器辅助模式"）：
/// 用户在浏览器里查看作业后，把课程与作业以 CSV 或 JSON 导入，本模式负责看板与提醒。
/// 该模式不抓取任何平台数据，完全离线。
/// </summary>
public sealed class LocalImportAdapter : IHomeworkPlatformAdapter
{
    public string Name => "本地/导入模式";

    public bool IsConfigured => true;

    public string CapabilityDescription =>
        "当前为本地/导入模式：不访问学习通服务器，仅对导入的作业计划做看板与提醒。";

    public Task<SyncOutcome> SyncAsync(Credential? credential, CancellationToken token)
        => Task.FromResult(new SyncOutcome(SyncResult.Degraded, CapabilityDescription, [], [], 0, 0));
}

/// <summary>
/// 通用 HTTP + JSON 适配器：通过可配置的端点与字段路径访问平台。
///
/// 说明：学习通未提供公开开放接口；本适配器是"可插拔接入点"——
/// 端点与字段映射完全由配置提供（settings 表），不在代码中内置任何非公开接口地址。
/// 一旦按抓包结果填好配置，登录、课程列表、作业列表即可正常工作；
/// 未配置时同步明确降级为本地模式，绝不返回伪造数据。
///
/// 频率控制：默认每 10 分钟最多一次请求，失败按指数退避（10 → 20 → 40 … 分钟，上限 6 小时）。
/// </summary>
public sealed class GenericJsonAdapter : IHomeworkPlatformAdapter
{
    private readonly AdapterConfig _config;
    private readonly HttpClient _http;
    private DateTime _lastRequestAt = DateTime.MinValue;
    private int _consecutiveFailures;

    public GenericJsonAdapter(AdapterConfig config)
    {
        _config = config;
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    public string Name => "HTTP/JSON 适配器";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.CoursePath) &&
        !string.IsNullOrWhiteSpace(_config.HomeworkPath) &&
        !string.IsNullOrWhiteSpace(_config.AccountField);

    public string CapabilityDescription => IsConfigured
        ? $"已配置平台端点：{_config.BaseUrl}（课程 {_config.CoursePath}，作业 {_config.HomeworkPath}）"
        : "尚未配置平台端点。学习通未提供公开开放接口，请在设置中按抓包结果填入端点与字段映射，" +
          "或使用「本地/导入模式」手动录入作业。";

    /// <summary>最小请求间隔（默认 10 分钟，符合"请求频率不低于每 10 分钟一次"的约束）。</summary>
    public TimeSpan MinInterval => TimeSpan.FromMinutes(10);

    /// <summary>指数退避后的下次允许时间。</summary>
    public DateTime NextAllowedAt
    {
        get
        {
            var backoff = _consecutiveFailures == 0
                ? MinInterval
                : TimeSpan.FromMinutes(Math.Min(360, 10 * Math.Pow(2, Math.Min(5, _consecutiveFailures))));
            return _lastRequestAt + backoff;
        }
    }

    public async Task<SyncOutcome> SyncAsync(Credential? credential, CancellationToken token)
    {
        var started = DateTime.Now;
        var requests = 0;

        if (!IsConfigured)
        {
            return new SyncOutcome(SyncResult.Degraded, CapabilityDescription, [], [], 0, 0);
        }

        if (credential is null)
        {
            return new SyncOutcome(SyncResult.SessionExpired, "缺少登录凭据，请在「学习通」页登录。", [], [], 0, 0);
        }

        if (DateTime.Now < NextAllowedAt)
        {
            var wait = NextAllowedAt - DateTime.Now;
            return new SyncOutcome(SyncResult.Skipped,
                $"请求频率受控，距下次允许请求还有 {wait.TotalMinutes:0.#} 分钟（失败退避中，连续失败 {_consecutiveFailures} 次）。",
                [], [], 0, 0);
        }

        try
        {
            // 1) 登录
            var loginPayload = new Dictionary<string, object?>
            {
                [_config.AccountField] = credential.Account,
                [_config.PasswordField] = credential.Password,
            };
            foreach (var extra in _config.LoginExtraFields) loginPayload[extra.Key] = extra.Value;

            var loginResponse = await PostJsonAsync(_config.Resolve(_config.LoginPath), loginPayload, token)
                .ConfigureAwait(false);
            requests++;
            _lastRequestAt = DateTime.Now;

            if (!loginResponse.Success)
            {
                _consecutiveFailures++;
                return new SyncOutcome(SyncResult.Failed,
                    $"登录请求失败（HTTP {loginResponse.StatusCode}）：{loginResponse.Error}。已按指数退避延后重试。",
                    [], [], (long)(DateTime.Now - started).TotalMilliseconds, requests);
            }

            if (LooksLikeAuthFailure(loginResponse.Body))
            {
                _consecutiveFailures = 5;
                return new SyncOutcome(SyncResult.SessionExpired,
                    "平台返回的响应表明会话已失效或凭据不正确。请重新登录（不会静默重试）。",
                    [], [], (long)(DateTime.Now - started).TotalMilliseconds, requests);
            }

            // 2) 课程列表
            var coursesResponse = await GetJsonAsync(_config.Resolve(_config.CoursePath), token).ConfigureAwait(false);
            requests++;
            var courses = ParseCourses(coursesResponse.Body);

            // 3) 作业列表
            var homeworkResponse = await GetJsonAsync(_config.Resolve(_config.HomeworkPath), token).ConfigureAwait(false);
            requests++;
            var homework = ParseHomework(homeworkResponse.Body, courses);

            _consecutiveFailures = 0;
            var message = $"同步成功：课程 {courses.Count} 门，作业 {homework.Count} 条（{requests} 次请求）。";
            return new SyncOutcome(SyncResult.Success, message, courses, homework,
                (long)(DateTime.Now - started).TotalMilliseconds, requests);
        }
        catch (OperationCanceledException)
        {
            return new SyncOutcome(SyncResult.Skipped, "同步已取消。", [], [],
                (long)(DateTime.Now - started).TotalMilliseconds, requests);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _lastRequestAt = DateTime.Now;
            Log.Warn($"学习通同步异常：{ex.Message}");
            return new SyncOutcome(SyncResult.Failed,
                $"同步失败：{ex.Message}。已按指数退避延后重试（不静默重试）。",
                [], [], (long)(DateTime.Now - started).TotalMilliseconds, requests);
        }
    }

    private sealed record HttpResult(bool Success, int StatusCode, string Body, string? Error);

    private async Task<HttpResult> PostJsonAsync(string url, Dictionary<string, object?> payload, CancellationToken token)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return new HttpResult(response.IsSuccessStatusCode, (int)response.StatusCode, body, null);
        }
        catch (Exception ex)
        {
            return new HttpResult(false, 0, string.Empty, ex.Message);
        }
    }

    private async Task<HttpResult> GetJsonAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return new HttpResult(response.IsSuccessStatusCode, (int)response.StatusCode, body, null);
        }
        catch (Exception ex)
        {
            return new HttpResult(false, 0, string.Empty, ex.Message);
        }
    }

    private static bool LooksLikeAuthFailure(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        string[] markers = ["未登录", "请先登录", "登录已过期", "not logged", "unauthorized", "invalid token", "会话失效"];
        return markers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ JSON 解析（字段路径可配置）

    private List<Course> ParseCourses(string body)
    {
        var list = new List<Course>();
        foreach (var node in ExtractArray(body, _config.CourseArrayPath))
        {
            var id = ReadString(node, _config.CourseIdField);
            var name = ReadString(node, _config.CourseNameField);
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new Course
            {
                Id = string.IsNullOrWhiteSpace(id) ? name : id,
                Name = name,
                Teacher = ReadString(node, _config.CourseTeacherField),
                Term = ReadString(node, _config.CourseTermField),
                Source = Name,
                SyncTime = DateTime.Now,
            });
        }
        return list;
    }

    private List<HomeworkEntry> ParseHomework(string body, List<Course> courses)
    {
        var list = new List<HomeworkEntry>();
        var courseNames = courses.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var node in ExtractArray(body, _config.HomeworkArrayPath))
        {
            var id = ReadString(node, _config.HomeworkIdField);
            var title = ReadString(node, _config.HomeworkTitleField);
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(id)) continue;

            var courseId = ReadString(node, _config.HomeworkCourseIdField);
            var deadlineText = ReadString(node, _config.HomeworkDeadlineField);
            var statusText = ReadString(node, _config.HomeworkStatusField);

            list.Add(new HomeworkEntry
            {
                Id = string.IsNullOrWhiteSpace(id) ? $"{courseId}:{title}" : id,
                CourseId = courseId,
                CourseName = courseNames.TryGetValue(courseId, out var cn) ? cn : courseId,
                Title = title,
                Description = ReadString(node, _config.HomeworkDescriptionField),
                Deadline = ParseDeadline(deadlineText),
                PlatformStatus = statusText,
                PlatformState = NormalizeStatus(statusText),
                SubmittedAt = ParseDeadline(ReadString(node, _config.HomeworkSubmittedAtField)),
                Source = Name,
                SyncTime = DateTime.Now,
            });
        }
        return list;
    }

    /// <summary>把平台原始状态归一化，便于与本地标记合并展示。</summary>
    public static string NormalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var text = raw.Trim().ToLowerInvariant();
        if (text is "1" or "true" or "submitted" or "已提交" or "已完成" or "已交" or "已交作业") return "submitted";
        if (text is "0" or "false" or "not_submitted" or "未提交" or "未交" or "待提交" or "未完成") return "not_submitted";
        if (text.Contains("逾期") || text.Contains("过期") || text.Contains("overdue") || text.Contains("expired")) return "overdue";
        if (text.Contains("已提交") || text.Contains("已交")) return "submitted";
        if (text.Contains("未提交") || text.Contains("未交") || text.Contains("待交")) return "not_submitted";
        return "unknown";
    }

    public static DateTime? ParseDeadline(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd",
            "yyyy年MM月dd日 HH:mm", "yyyy年MM月dd日",
        ];
        if (DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
            return exact;
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var loose))
            return loose;
        if (long.TryParse(text, out var unix))
        {
            try
            {
                var value = unix > 1_000_000_000_000 ? unix / 1000 : unix;
                return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>按点分路径取出数组（支持 <c>data.list</c> 这类嵌套；空路径表示根即数组）。</summary>
    private static List<JsonElement> ExtractArray(string body, string path)
    {
        var result = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(body)) return result;
        try
        {
            using var document = JsonDocument.Parse(body);
            var current = document.RootElement;
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (current.ValueKind == JsonValueKind.Array)
                    {
                        // 常见包装：{ data: { list: [...] } } 或 { data: [...] }
                        current = current.EnumerateArray().FirstOrDefault();
                    }
                    if (current.ValueKind != JsonValueKind.Object) return result;
                    if (!current.TryGetProperty(segment, out var next)) return result;
                    current = next;
                }
            }

            if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) result.Add(item.Clone());
            }
            else if (current.ValueKind == JsonValueKind.Object)
            {
                result.Add(current.Clone());
            }
        }
        catch (JsonException)
        {
            // 响应不是 JSON：交由上层按失败处理
        }
        return result;
    }

    private static string ReadString(JsonElement node, string field)
    {
        if (string.IsNullOrWhiteSpace(field) || node.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!node.TryGetProperty(field, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }
}

/// <summary>适配器配置（由用户在设置中填写，持久化在 settings 表）。</summary>
public sealed class AdapterConfig
{
    public string BaseUrl { get; set; } = string.Empty;
    public string LoginPath { get; set; } = "/api/login";
    public string CoursePath { get; set; } = "/api/courses";
    public string HomeworkPath { get; set; } = "/api/homework";
    public string AccountField { get; set; } = "phone";
    public string PasswordField { get; set; } = "password";
    public Dictionary<string, string> LoginExtraFields { get; set; } = new();
    public string CourseArrayPath { get; set; } = "data";
    public string CourseIdField { get; set; } = "id";
    public string CourseNameField { get; set; } = "name";
    public string CourseTeacherField { get; set; } = "teacher";
    public string CourseTermField { get; set; } = "term";
    public string HomeworkArrayPath { get; set; } = "data";
    public string HomeworkIdField { get; set; } = "id";
    public string HomeworkTitleField { get; set; } = "title";
    public string HomeworkCourseIdField { get; set; } = "courseId";
    public string HomeworkDeadlineField { get; set; } = "deadline";
    public string HomeworkStatusField { get; set; } = "status";
    public string HomeworkSubmittedAtField { get; set; } = "submittedAt";
    public string HomeworkDescriptionField { get; set; } = "description";

    public string Resolve(string path)
    {
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
        return BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    public static AdapterConfig Load(Data.Database db)
    {
        var raw = db.GetSetting("xuexitong.adapter");
        if (string.IsNullOrWhiteSpace(raw)) return new AdapterConfig();
        try
        {
            return JsonSerializer.Deserialize(raw, HomeworkJsonContext.Default.AdapterConfig) ?? new AdapterConfig();
        }
        catch
        {
            return new AdapterConfig();
        }
    }

    public void Save(Data.Database db)
        => db.SetSetting("xuexitong.adapter", JsonSerializer.Serialize(this, HomeworkJsonContext.Default.AdapterConfig));
}

/// <summary>JSON 源生成上下文（避免运行时反射，兼容裁剪与单文件发布）。</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Credential))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AdapterConfig))]
internal partial class HomeworkJsonContext : JsonSerializerContext
{
}
