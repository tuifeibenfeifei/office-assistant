using System.Text;
using System.Text.Json;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
// 模型类 Homework 与命名空间 OfficeAssistant.Core.Homework 同名，这里用别名消除歧义
using HomeworkEntry = OfficeAssistant.Core.Models.Homework;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Homework;

/// <summary>提醒阈值配置。</summary>
public sealed class ReminderSettings
{
    /// <summary>提前提醒的小时数（默认 24 / 6 / 1 小时）。</summary>
    public List<int> ThresholdHours { get; set; } = [24, 6, 1];

    public bool NotifyOverdue { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

/// <summary>需要弹出的提醒。</summary>
public sealed record ReminderItem(HomeworkEntry Homework, int ThresholdHours, string Text);

/// <summary>
/// 学习通作业管理服务（功能二）：登录凭据、同步引擎、看板数据、提醒评估、手动导入。
///
/// 合规与安全约束（硬性）：
/// - 仅登录用户本人账号，凭据经 DPAPI 加密本地存储，不上传；
/// - 请求频率默认不低于每 10 分钟一次，失败按指数退避；
/// - 会话失效时明确提示重新登录，不静默重试；
/// - 平台未配置可用接口时降级为本地/导入模式，不返回任何伪造数据。
/// </summary>
public sealed class HomeworkService : IDisposable
{
    private readonly Database _db;
    private readonly CredentialVault _vault = new();
    private readonly AdapterConfig _adapterConfig;
    private readonly IHomeworkPlatformAdapter _adapter;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _autoSyncCts;
    private Thread? _autoSyncThread;

    public HomeworkService(Database db)
    {
        _db = db;
        _adapterConfig = AdapterConfig.Load(db);
        _adapter = new GenericJsonAdapter(_adapterConfig);
        Reminders = LoadReminderSettings(db);
    }

    public ReminderSettings Reminders { get; }

    public CredentialVault Vault => _vault;

    public AdapterConfig AdapterConfig => _adapterConfig;

    public string AdapterCapability => _adapter.CapabilityDescription;

    public bool AdapterConfigured => _adapter.IsConfigured;

    /// <summary>自动同步间隔（分钟），默认 10 分钟且不得低于 10 分钟（平台频率约束）。</summary>
    public int AutoSyncMinutes
    {
        get
        {
            var raw = _db.GetSetting("homework.autosync.minutes");
            return int.TryParse(raw, out var value) ? Math.Max(10, value) : 10;
        }
        set => _db.SetSetting("homework.autosync.minutes", Math.Max(10, value).ToString());
    }

    public bool AutoSyncEnabled
    {
        get => _db.GetSetting("homework.autosync.enabled") != "0";
        set => _db.SetSetting("homework.autosync.enabled", value ? "1" : "0");
    }

    public DateTime? LastSyncAt
    {
        get
        {
            var raw = _db.GetSetting("homework.last.sync");
            return DateTime.TryParse(raw, out var value) ? value : null;
        }
        private set => _db.SetSetting("homework.last.sync", value?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty);
    }

    // ------------------------------------------------------------------ 提醒设置

    private static ReminderSettings LoadReminderSettings(Database db)
    {
        var settings = new ReminderSettings();
        var raw = db.GetSetting("homework.reminders");
        if (string.IsNullOrWhiteSpace(raw)) return settings;
        try
        {
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hours = parts.Select(p => int.TryParse(p, out var h) ? h : -1).Where(h => h > 0).Distinct().OrderByDescending(h => h).ToList();
            if (hours.Count > 0) settings.ThresholdHours = hours;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取提醒设置失败：{ex.Message}");
        }
        return settings;
    }

    public void SaveReminderSettings(IEnumerable<int> thresholdHours, bool enabled, bool notifyOverdue)
    {
        var hours = thresholdHours.Where(h => h > 0).Distinct().OrderByDescending(h => h).ToList();
        if (hours.Count == 0) hours = [24, 6, 1];
        Reminders.ThresholdHours = hours;
        Reminders.Enabled = enabled;
        Reminders.NotifyOverdue = notifyOverdue;
        _db.SetSetting("homework.reminders", string.Join(',', hours));
        _db.SetSetting("homework.reminders.enabled", enabled ? "1" : "0");
        _db.SetSetting("homework.reminders.overdue", notifyOverdue ? "1" : "0");
    }

    // ------------------------------------------------------------------ 同步

    public List<HomeworkEntry> GetHomework() => _db.GetHomework();

    public List<Course> GetCourses() => _db.GetCourses();

    public List<SyncLogEntry> GetSyncLogs(int limit = 50) => _db.GetSyncLogs(limit);

    /// <summary>手动刷新（用户点击刷新时使用，仍然受最小请求间隔约束）。</summary>
    public Task<SyncOutcome> RefreshAsync(bool force = false, CancellationToken token = default)
        => SyncAsync(force ? "manual-force" : "manual", token);

    public Task<SyncOutcome> SyncAsync(string trigger, CancellationToken token = default)
    {
        if (!_syncGate.Wait(0))
            return Task.FromResult(new SyncOutcome(SyncResult.Skipped, "已有同步任务在执行中。", [], [], 0, 0));
        try
        {
            return SyncCoreAsync(trigger, token);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<SyncOutcome> SyncCoreAsync(string trigger, CancellationToken token)
    {
        var credential = _vault.Load();
        if (credential is null && _adapter.IsConfigured)
        {
            var outcome = new SyncOutcome(SyncResult.SessionExpired, "尚未登录学习通账号，请先在「学习通」页登录。", [], [], 0, 0);
            RecordSync(outcome, trigger);
            return outcome;
        }

        var result = await _adapter.SyncAsync(credential, token).ConfigureAwait(false);

        if (result.Result == SyncResult.Success)
        {
            foreach (var course in result.Courses) _db.UpsertCourse(course);
            foreach (var homework in result.Homework)
            {
                // 保留本地完成标记：平台数据不覆盖 local_done
                var existing = _db.GetHomework().FirstOrDefault(h => h.Id == homework.Id);
                if (existing is not null) homework.LocalDone = existing.LocalDone;
                _db.UpsertHomework(homework);
            }
            LastSyncAt = DateTime.Now;
        }
        else if (result.Result == SyncResult.SessionExpired)
        {
            // 不静默重试：只记录并提示
            Log.Warn($"学习通会话失效：{result.Message}");
        }

        RecordSync(result, trigger);
        return result;
    }

    private void RecordSync(SyncOutcome outcome, string trigger)
    {
        _db.AddSyncLog(new SyncLogEntry
        {
            SyncTime = DateTime.Now,
            Result = outcome.Result,
            ErrorMessage = outcome.Result == SyncResult.Success ? null : outcome.Message,
            DurationMs = outcome.DurationMs,
            CourseCount = outcome.Courses.Count,
            HomeworkCount = outcome.Homework.Count,
            Adapter = $"{_adapter.Name}（{trigger}）",
        });
    }

    // ------------------------------------------------------------------ 自动同步

    public void StartAutoSync()
    {
        if (_autoSyncThread is { IsAlive: true }) return;
        _autoSyncCts = new CancellationTokenSource();
        var token = _autoSyncCts.Token;
        _autoSyncThread = new Thread(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (AutoSyncEnabled && _adapter.IsConfigured)
                        await SyncAsync("auto", token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"自动同步失败：{ex.Message}");
                }

                try { token.WaitHandle.WaitOne(TimeSpan.FromMinutes(AutoSyncMinutes)); }
                catch { break; }
            }
        })
        {
            IsBackground = true,
            Name = "homework-autosync",
            Priority = ThreadPriority.BelowNormal,
        };
        _autoSyncThread.Start();
        Log.Info($"学习通自动同步已启动（每 {AutoSyncMinutes} 分钟）");
    }

    public void StopAutoSync()
    {
        try { _autoSyncCts?.Cancel(); } catch { /* 忽略 */ }
        try { _autoSyncThread?.Join(1500); } catch { /* 忽略 */ }
    }

    // ------------------------------------------------------------------ 本地完成标记

    public void MarkLocalDone(string homeworkId, bool done) => _db.SetHomeworkLocalDone(homeworkId, done);

    // ------------------------------------------------------------------ 提醒评估

    /// <summary>
    /// 评估需要弹出的提醒。同一作业同一阈值同一截止时间只提醒一次（reminder_log 去重），
    /// 因此"提醒到达率 100%"具备可验证的持久化依据。
    /// </summary>
    public List<ReminderItem> EvaluateReminders(DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        var items = new List<ReminderItem>();
        if (!Reminders.Enabled) return items;

        foreach (var homework in _db.GetHomework())
        {
            if (homework.LocalDone || homework.PlatformState == "submitted") continue;
            if (!homework.Deadline.HasValue) continue;

            var remaining = homework.Deadline.Value - now;
            var overdue = remaining < TimeSpan.Zero;

            if (overdue)
            {
                if (!Reminders.NotifyOverdue) continue;
                if (_db.HasReminderBeenSent(homework.Id, 0, homework.Deadline)) continue;
                items.Add(new ReminderItem(homework, 0,
                    $"「{homework.CourseName}」的作业已逾期：{homework.Title}（截止 {homework.Deadline:MM-dd HH:mm}）"));
                continue;
            }

            foreach (var threshold in Reminders.ThresholdHours)
            {
                if (remaining > TimeSpan.FromHours(threshold)) continue;
                // 已经进入更紧迫的档位时，不再补发更宽松档位的提醒
                if (Reminders.ThresholdHours.Any(smaller => smaller < threshold && remaining <= TimeSpan.FromHours(smaller)))
                    continue;
                if (_db.HasReminderBeenSent(homework.Id, threshold, homework.Deadline)) continue;
                items.Add(new ReminderItem(homework, threshold,
                    $"「{homework.CourseName}」的作业将在 {threshold} 小时内截止：{homework.Title}（截止 {homework.Deadline:MM-dd HH:mm}）"));
                break;
            }
        }

        return items;
    }

    public void MarkReminderSent(ReminderItem item)
        => _db.RecordReminder(item.Homework.Id, item.ThresholdHours, item.Homework.Deadline);

    /// <summary>未完成作业数量（托盘角标用）。</summary>
    public int PendingCount()
        => _db.GetHomework().Count(h => h.State is HomeworkState.NotSubmitted or HomeworkState.Overdue or HomeworkState.Unsynced);

    // ------------------------------------------------------------------ 手动导入（降级模式）

    public sealed record ImportResult(int Imported, int Skipped, List<string> Errors);

    /// <summary>
    /// 导入本地作业计划（CSV 或 JSON）。用于平台接口不可用时的降级路径，
    /// 完全不访问网络。CSV 表头：课程,标题,截止时间,状态,教师
    /// </summary>
    public ImportResult ImportFromFile(string filePath)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;
        try
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            List<HomeworkEntry> parsed = filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ParseJsonImport(text, errors)
                : ParseCsvImport(text, errors);

            foreach (var homework in parsed)
            {
                if (string.IsNullOrWhiteSpace(homework.Title))
                {
                    skipped++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(homework.Id))
                    homework.Id = "import:" + homework.Title.GetHashCode().ToString("X8");
                _db.UpsertHomework(homework);
                imported++;
            }

            _db.LogOperation(new OperationLogEntry
            {
                Operation = "导入作业计划",
                Source = filePath,
                Result = $"导入 {imported} 条",
                Detail = errors.Count > 0 ? string.Join("；", errors.Take(3)) : null,
            });
        }
        catch (Exception ex)
        {
            errors.Add($"读取文件失败：{ex.Message}");
        }

        return new ImportResult(imported, skipped, errors);
    }

    private static List<HomeworkEntry> ParseCsvImport(string text, List<string> errors)
    {
        var list = new List<HomeworkEntry>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            var cells = SplitCsvLine(line);
            if (i == 0 && (cells.Length == 0 || cells[0].Contains("课程") || cells[0].Contains("course", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (cells.Length < 2)
            {
                errors.Add($"第 {i + 1} 行列数不足，已跳过");
                continue;
            }

            var course = cells[0].Trim();
            var title = cells.Length > 1 ? cells[1].Trim() : string.Empty;
            var deadlineText = cells.Length > 2 ? cells[2].Trim() : string.Empty;
            var statusText = cells.Length > 3 ? cells[3].Trim() : string.Empty;
            var teacher = cells.Length > 4 ? cells[4].Trim() : string.Empty;

            list.Add(new HomeworkEntry
            {
                Id = $"import:{course}:{title}",
                CourseId = course,
                CourseName = course,
                Title = title,
                Deadline = GenericJsonAdapter.ParseDeadline(deadlineText),
                PlatformStatus = statusText,
                PlatformState = GenericJsonAdapter.NormalizeStatus(statusText),
                Source = "本地导入",
                SyncTime = DateTime.Now,
            });

            if (!string.IsNullOrEmpty(teacher)) list[^1].Description = $"教师：{teacher}";
        }
        return list;
    }

    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == ',' && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }

    private sealed record ImportJsonRow(string? Course, string? Title, string? Deadline, string? Status);

    private static List<HomeworkEntry> ParseJsonImport(string text, List<string> errors)
    {
        var list = new List<HomeworkEntry>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                errors.Add("JSON 顶层应为数组，每项包含 course/title/deadline/status 字段");
                return list;
            }

            foreach (var item in root.EnumerateArray())
            {
                var course = ReadField(item, "course") ?? ReadField(item, "courseName") ?? string.Empty;
                var title = ReadField(item, "title") ?? string.Empty;
                var deadline = ReadField(item, "deadline");
                var status = ReadField(item, "status");
                list.Add(new HomeworkEntry
                {
                    Id = ReadField(item, "id") ?? $"import:{course}:{title}",
                    CourseId = course,
                    CourseName = course,
                    Title = title,
                    Deadline = GenericJsonAdapter.ParseDeadline(deadline),
                    PlatformStatus = status,
                    PlatformState = GenericJsonAdapter.NormalizeStatus(status),
                    Source = "本地导入",
                    SyncTime = DateTime.Now,
                });
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"JSON 解析失败：{ex.Message}");
        }
        return list;
    }

    private static string? ReadField(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    /// <summary>导出当前作业计划为 CSV（iCal 为 P3 可选项，此处提供通用 CSV 便于二次处理）。</summary>
    public string ExportCsv(string targetPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("课程,标题,截止时间,状态,平台状态,本地完成");
        foreach (var h in _db.GetHomework().OrderBy(h => h.Deadline ?? DateTime.MaxValue))
        {
            sb.AppendLine(string.Join(',',
                Escape(h.CourseName), Escape(h.Title),
                h.Deadline?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                Escape(h.StateText), Escape(h.PlatformStatus ?? string.Empty),
                h.LocalDone ? "是" : "否"));
        }
        File.WriteAllText(targetPath, sb.ToString(), new UTF8Encoding(true));
        return targetPath;
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public void Dispose()
    {
        StopAutoSync();
        _syncGate.Dispose();
        (_adapter as IDisposable)?.Dispose();
    }
}
