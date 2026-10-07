using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Resources;

/// <summary>资源管家的调度配置（持久化在 settings 表）。</summary>
public sealed class ResourceSettings
{
    /// <summary>总计开关。关闭后调度器不再自动执行任何清理。</summary>
    public bool Enabled { get; set; }

    /// <summary>调度模式：interval（周期）或 cron（固定时间）。</summary>
    public string Mode { get; set; } = "interval";

    public int IntervalMinutes { get; set; } = 30;

    public string CronExpression { get; set; } = "0 12 * * *";

    /// <summary>阈值触发：内存占用高于该百分比。</summary>
    public int MemoryThresholdPercent { get; set; } = 80;

    /// <summary>阈值触发的前置条件：系统空闲超过该分钟数。</summary>
    public int IdleMinutesRequired { get; set; } = 5;

    /// <summary>是否启用阈值触发（与周期调度叠加）。</summary>
    public bool ThresholdTriggerEnabled { get; set; } = true;

    public bool PurgeStandbyList { get; set; }

    public bool LowPriorityStandbyOnly { get; set; } = true;

    public bool AdjustPriority { get; set; } = true;

    public long MinWorkingSetBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>前台进程豁免（固定开启，仅作显式记录）。</summary>
    public bool ForegroundExempt { get; set; } = true;

    public DateTime LastRunAt { get; set; }

    public DateTime LastThresholdCheckAt { get; set; }

    public static ResourceSettings Load(Database db)
    {
        var raw = db.GetSetting("resource.settings");
        if (string.IsNullOrWhiteSpace(raw)) return new ResourceSettings();
        try
        {
            return JsonSerializer.Deserialize(raw, ResourceJsonContext.Default.ResourceSettings) ?? new ResourceSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取资源管家设置失败，使用默认值：{ex.Message}");
            return new ResourceSettings();
        }
    }

    public void Save(Database db)
        => db.SetSetting("resource.settings", JsonSerializer.Serialize(this, ResourceJsonContext.Default.ResourceSettings));

    public ResourceCleanupOptions ToOptions(string trigger) => new()
    {
        Trigger = trigger,
        PurgeStandbyList = PurgeStandbyList,
        LowPriorityStandbyOnly = LowPriorityStandbyOnly,
        AdjustPriority = AdjustPriority,
        MinWorkingSetBytes = MinWorkingSetBytes,
    };

    public string DescribeSchedule()
    {
        if (!Enabled) return "未启用";
        if (Mode == "cron")
        {
            // 注意：属性 CronExpression 与类型 CronExpression 同名，这里显式写出类型全名以避免歧义
            return OfficeAssistant.Core.Resources.CronExpression.TryParse(CronExpression, out var cron, out _)
                ? $"固定时间：{cron!.Expression}（分 时 日 月 周）"
                : $"固定时间：{CronExpression}（表达式无效）";
        }
        return new IntervalSchedule { IntervalMinutes = IntervalMinutes }.Describe();
    }
}

/// <summary>
/// 资源管家调度器：周期/固定时间调度 + 阈值触发（内存高于阈值且系统空闲超过设定时长）。
/// 常驻后台线程，托盘运行时无需用户点击；调度误差目标 ≤ 60 秒。
/// </summary>
public sealed class ResourceScheduler : IDisposable
{
    private readonly Database _db;
    private readonly ResourceOptimizer _optimizer;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private string _lastCronMinute = string.Empty;

    public ResourceScheduler(Database db, ResourceOptimizer optimizer)
    {
        _db = db;
        _optimizer = optimizer;
        Settings = ResourceSettings.Load(db);
    }

    public ResourceSettings Settings { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>最近一次清理报告（内存中保留，供面板展示）。</summary>
    public CleanupReport? LastReport { get; private set; }

    public List<CleanupReport> RecentReports { get; } = [];

    /// <summary>阈值触发评估的实时状态，供 UI 展示"为什么没触发"。</summary>
    public string LastEvaluation { get; private set; } = "尚未评估";

    public event Action<CleanupReport>? CleanupCompleted;

    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (IsRunning) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "resource-scheduler",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        Log.Info($"资源管家调度器已启动：{Settings.DescribeSchedule()}");
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { /* 忽略 */ }
        try { _thread?.Join(2000); } catch { /* 忽略 */ }
        Log.Info("资源管家调度器已停止");
    }

    public void Reload()
    {
        Settings = ResourceSettings.Load(_db);
        StatusChanged?.Invoke($"调度配置已更新：{Settings.DescribeSchedule()}");
    }

    private void Loop()
    {
        // 每 20 秒检查一次，保证调度误差远小于 60 秒目标
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("资源管家调度周期异常", ex);
            }

            try { _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); }
            catch { break; }
        }
    }

    public void Tick(DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        if (!Settings.Enabled)
        {
            LastEvaluation = "自动清理未启用";
            return;
        }

        var memory = _optimizer.ReadMemory();
        var idle = ResourceOptimizer.GetIdleTime();
        var due = false;
        var trigger = string.Empty;

        // 1) 周期 / 固定时间调度
        if (Settings.Mode == "cron")
        {
            if (CronExpression.TryParse(Settings.CronExpression, out var cron, out _) &&
                cron!.Matches(now) &&
                _lastCronMinute != CronExpression.MinuteKey(now))
            {
                _lastCronMinute = CronExpression.MinuteKey(now);
                due = true;
                trigger = "定时执行";
            }
        }
        else
        {
            var schedule = new IntervalSchedule { IntervalMinutes = Settings.IntervalMinutes };
            if (schedule.IsDue(Settings.LastRunAt, now))
            {
                due = true;
                trigger = "周期执行";
            }
        }

        // 2) 阈值触发：内存占用高于阈值 且 系统空闲超过设定时长
        if (!due && Settings.ThresholdTriggerEnabled)
        {
            var overThreshold = memory.UsedPercent >= Settings.MemoryThresholdPercent;
            var idleEnough = idle.TotalMinutes >= Settings.IdleMinutesRequired;
            // 阈值触发也要有最小间隔，避免频繁执行
            var cooldownOk = Settings.LastRunAt == default || (now - Settings.LastRunAt).TotalMinutes >= 5;

            if (overThreshold && idleEnough && cooldownOk)
            {
                due = true;
                trigger = "阈值触发";
            }
            else
            {
                LastEvaluation =
                    $"当前内存 {memory.UsedPercent:0.#}%（阈值 {Settings.MemoryThresholdPercent}%），" +
                    $"空闲 {(int)idle.TotalMinutes} 分钟（需 {Settings.IdleMinutesRequired} 分钟）→ " +
                    (!overThreshold ? "未达内存阈值" : !idleEnough ? "系统仍在使用中" : "冷却中");
            }
        }
        else if (!due)
        {
            LastEvaluation = $"等待调度：{Settings.DescribeSchedule()}";
        }

        Settings.LastThresholdCheckAt = now;
        if (!due) return;

        LastEvaluation = $"已触发（{trigger}）";
        RunNow(trigger);
    }

    /// <summary>立即执行一次清理（手动按钮与调度共用）。</summary>
    public CleanupReport RunNow(string trigger)
    {
        StatusChanged?.Invoke($"正在执行内存整理（{trigger}）…");
        var options = Settings.ToOptions(trigger);
        var report = _optimizer.RunCleanup(options);

        Settings.LastRunAt = DateTime.Now;
        Settings.Save(_db);

        LastReport = report;
        lock (RecentReports)
        {
            RecentReports.Insert(0, report);
            if (RecentReports.Count > 20) RecentReports.RemoveRange(20, RecentReports.Count - 20);
        }

        StatusChanged?.Invoke(report.Summary);
        try { CleanupCompleted?.Invoke(report); } catch { /* 忽略 UI 异常 */ }
        return report;
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}

/// <summary>JSON 源生成上下文（避免运行时反射，兼容裁剪/单文件发布）。</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(ResourceSettings))]
internal partial class ResourceJsonContext : JsonSerializerContext
{
}
