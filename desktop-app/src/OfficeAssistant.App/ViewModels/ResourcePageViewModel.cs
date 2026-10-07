using System.Collections.ObjectModel;
using System.Diagnostics;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Resources;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>进程行。</summary>
public sealed class ProcessRowViewModel
{
    public ProcessRowViewModel(ProcessInfo info, bool whitelisted, bool critical)
    {
        Info = info;
        IsWhitelisted = whitelisted;
        IsCritical = critical;
    }

    public ProcessInfo Info { get; }
    public bool IsWhitelisted { get; }
    public bool IsCritical { get; }

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string MemoryText => Format.Bytes(Info.WorkingSetBytes);
    public string PrivateText => Format.Bytes(Info.PrivateBytes);
    public string CpuText => Format.Duration(Info.CpuTime);
    public string IdleText => Info.IdleTime == TimeSpan.Zero ? "—" : Format.Duration(Info.IdleTime);
    public string RoleText => Info.IsForeground ? "前台（豁免）"
        : IsCritical ? "系统关键（豁免）"
        : IsWhitelisted ? "白名单（豁免）"
        : "可整理";
    public string ProtectionText => Info.IsForeground || IsCritical || IsWhitelisted ? "豁免" : "参与整理";
}

/// <summary>清理历史行。</summary>
public sealed class ReportRowViewModel
{
    public ReportRowViewModel(CleanupReport report) => Report = report;

    public CleanupReport Report { get; }
    public string TimeText => Report.StartedAt.ToString("MM-dd HH:mm:ss");
    public string Trigger => Report.Trigger;
    public string ReleasedText => Format.Bytes(Report.WorkingSetReleasedBytes);
    public string StandbyText => Report.StandbyPurged ? Format.Bytes(Report.StandbyReleasedBytes) : "未启用";
    public string DurationText => Format.Duration(Report.Seconds);
    public string DetailText =>
        $"压缩 {Report.CompressedProcesses} 个进程，跳过 {Report.SkippedProcesses} 个（前台/白名单/关键/小工作集），失败 {Report.FailedProcesses} 个；“清理前”内存 {Format.Bytes((long)Report.MemoryBefore)} → “清理后” {Format.Bytes((long)Report.MemoryAfter)}";
}

/// <summary>
/// 智能资源管家页视图模型（功能四）。
///
/// 如实呈现技术边界：工作集压缩是短期整理，系统按需会重新调入页面，
/// 因此面板始终并列展示"清理前 / 清理后"与释放量，不承诺持续低占用。
/// </summary>
public sealed class ResourcePageViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ResourceOptimizer _optimizer;
    private readonly ResourceScheduler _scheduler;
    private readonly Stopwatch _refreshThrottle = Stopwatch.StartNew();

    private double _usedPercent;
    private string _memoryText = "正在读取系统内存…";
    private string _standbyText = "—";
    private string _schedulerText = "—";
    private string _evaluationText = "—";
    private string _lastReportText = "尚未执行过内存整理。";
    private string _idleText = "—";
    private string _whitelistInput = string.Empty;
    private bool _enabled;
    private string _mode = "interval";
    private int _intervalMinutes = 30;
    private string _cronExpression = "0 12 * * *";
    private int _thresholdPercent = 80;
    private int _idleMinutes = 5;
    private bool _thresholdTrigger = true;
    private bool _purgeStandby;
    private bool _lowPriorityStandby = true;
    private bool _adjustPriority = true;
    private bool _isRunning;
    private string _statusText = "按调度自动执行，无需手动点击。";

    public ResourcePageViewModel(AppHost host)
    {
        _host = host;
        _optimizer = host.ResourceOptimizer;
        _scheduler = host.ResourceScheduler;

        var settings = _scheduler.Settings;
        _enabled = settings.Enabled;
        _mode = settings.Mode;
        _intervalMinutes = settings.IntervalMinutes;
        _cronExpression = settings.CronExpression;
        _thresholdPercent = settings.MemoryThresholdPercent;
        _idleMinutes = settings.IdleMinutesRequired;
        _thresholdTrigger = settings.ThresholdTriggerEnabled;
        _purgeStandby = settings.PurgeStandbyList;
        _lowPriorityStandby = settings.LowPriorityStandbyOnly;
        _adjustPriority = settings.AdjustPriority;
        _whitelistInput = string.Join(", ", _optimizer.Whitelist.Where(w => !ResourceOptimizer.DefaultWhitelist.Contains(w, StringComparer.OrdinalIgnoreCase)));

        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => !IsRunning);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRunning);
        SaveCommand = new RelayCommand(SaveSettings);
        SaveWhitelistCommand = new RelayCommand(SaveWhitelist);

        _scheduler.StatusChanged += OnSchedulerStatus;
        _scheduler.CleanupCompleted += OnCleanupCompleted;
    }

    public AsyncRelayCommand RunNowCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveWhitelistCommand { get; }

    public ObservableCollection<ProcessRowViewModel> TopProcesses { get; } = [];

    public ObservableCollection<ReportRowViewModel> Reports { get; } = [];

    public Func<string, string, bool>? RequestConfirm { get; set; }

    public string BoundaryNotice => CleanupReport.BoundaryNotice;

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public double UsedPercent
    {
        get => _usedPercent;
        private set => SetProperty(ref _usedPercent, value);
    }

    public string StandbyText
    {
        get => _standbyText;
        private set => SetProperty(ref _standbyText, value);
    }

    public string IdleText
    {
        get => _idleText;
        private set => SetProperty(ref _idleText, value);
    }

    public string SchedulerText
    {
        get => _schedulerText;
        private set => SetProperty(ref _schedulerText, value);
    }

    public string EvaluationText
    {
        get => _evaluationText;
        private set => SetProperty(ref _evaluationText, value);
    }

    public string LastReportText
    {
        get => _lastReportText;
        private set => SetProperty(ref _lastReportText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RunNowCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 累计已释放内存（MB）。内存清理界面（文档 3.4 界面 4）环心展示的就是这个数字，
    /// 口径为调度器保留的全部清理报告的工作集释放量之和。
    /// </summary>
    public string TotalReleasedMb
    {
        get
        {
            long total = 0;
            lock (_host.ResourceScheduler.RecentReports)
            {
                foreach (var report in _host.ResourceScheduler.RecentReports)
                    total += report.WorkingSetReleasedBytes;
            }

            return (total / (1024d * 1024d)).ToString("0");
        }
    }

    public bool IsElevated => _host.IsElevated;

    public string ElevationText => IsElevated
        ? "当前以管理员身份运行：可执行待机列表清理。"
        : "当前为普通权限：工作集压缩可用；待机列表清理需要以管理员身份重新启动本程序。";

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsCronMode));
        }
    }

    /// <summary>只读派生属性：是否使用 cron 固定时间调度（界面上为一对互斥单选按钮）。</summary>
    public bool IsCronMode => Mode == "cron";

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => SetProperty(ref _intervalMinutes, Math.Max(1, value));
    }

    public string CronExpressionText
    {
        get => _cronExpression;
        set => SetProperty(ref _cronExpression, value);
    }

    public int ThresholdPercent
    {
        get => _thresholdPercent;
        set => SetProperty(ref _thresholdPercent, Math.Clamp(value, 10, 99));
    }

    public int IdleMinutes
    {
        get => _idleMinutes;
        set => SetProperty(ref _idleMinutes, Math.Clamp(value, 0, 240));
    }

    public bool ThresholdTrigger
    {
        get => _thresholdTrigger;
        set => SetProperty(ref _thresholdTrigger, value);
    }

    public bool PurgeStandby
    {
        get => _purgeStandby;
        set => SetProperty(ref _purgeStandby, value);
    }

    public bool LowPriorityStandby
    {
        get => _lowPriorityStandby;
        set => SetProperty(ref _lowPriorityStandby, value);
    }

    public bool AdjustPriority
    {
        get => _adjustPriority;
        set => SetProperty(ref _adjustPriority, value);
    }

    public string WhitelistInput
    {
        get => _whitelistInput;
        set => SetProperty(ref _whitelistInput, value);
    }

    public string WhitelistText =>
        $"默认不触碰：{string.Join("、", ResourceOptimizer.DefaultWhitelist.Take(12))}…（共 {ResourceOptimizer.DefaultWhitelist.Length} 项系统关键与前台常用进程）";

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    private void OnSchedulerStatus(string message)
        => System.Windows.Application.Current?.Dispatcher.Invoke(() => StatusText = message);
    private void OnCleanupCompleted(CleanupReport report)
        => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LastReportText = report.Summary;
            Reports.Insert(0, new ReportRowViewModel(report));
            while (Reports.Count > 30) Reports.RemoveAt(Reports.Count - 1);
        });

    public async Task RefreshAsync()
    {
        if (_refreshThrottle.ElapsedMilliseconds < 200) return;
        _refreshThrottle.Restart();

        var (snapshot, idle, processes) = await Task.Run(() =>
        {
            var memory = _optimizer.ReadMemory();
            var idleTime = ResourceOptimizer.GetIdleTime();
            var snapshotProcesses = _optimizer.Snapshot()
                .Where(p => p.Pid > 4)
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(40)
                .ToList();
            return (memory, idleTime, snapshotProcesses);
        }).ConfigureAwait(true);

        UsedPercent = snapshot.UsedPercent;
        MemoryText = $"物理内存：{Format.Bytes((long)snapshot.PhysicalUsedBytes)} / {Format.Bytes((long)snapshot.TotalBytes)} 使用中（{snapshot.UsedPercent:0.#}%），" +
                     $"可用 {Format.Bytes((long)snapshot.AvailableBytes)}" +
                     (snapshot.CommittedBytes > 0 ? $"，已提交 {Format.Bytes((long)snapshot.CommittedBytes)}" : string.Empty);
        StandbyText = snapshot.StandbyBytes > 0
            ? $"待机缓存（Standby）：{Format.Bytes((long)snapshot.StandbyBytes)}"
            : "待机缓存：系统未提供该计数（不影响工作集压缩）";
        IdleText = $"系统空闲：{Format.Duration(idle)}（无键盘鼠标输入时长）";
        SchedulerText = $"调度：{_scheduler.Settings.DescribeSchedule()}；最近执行：{(_scheduler.Settings.LastRunAt == default ? "从未" : _scheduler.Settings.LastRunAt.ToString("MM-dd HH:mm:ss"))}";
        EvaluationText = _scheduler.LastEvaluation;

        TopProcesses.Clear();
        foreach (var info in processes)
        {
            TopProcesses.Add(new ProcessRowViewModel(info, _optimizer.IsWhitelisted(info.Name), info.IsCritical));
        }
    }

    private async Task RunNowAsync()
    {
        var confirmed = RequestConfirm?.Invoke("立即执行内存整理",
            "将对非前台、非白名单进程执行工作集压缩（EmptyWorkingSet），并降低长时间低交互进程的优先级（结束后恢复）。\n\n" +
            "不会结束任何进程。前台应用与白名单进程自动豁免。是否继续？") ?? true;
        if (!confirmed) return;

        IsRunning = true;
        StatusText = "正在执行内存整理…";
        try
        {
            var report = await Task.Run(() => _scheduler.RunNow("手动触发")).ConfigureAwait(true);
            LastReportText = report.Summary;
            Reports.Insert(0, new ReportRowViewModel(report));
            StatusText = report.Summary;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"执行失败：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
            // 圆环数字（累计已释放）依赖本次新增的报告，必须显式通知更新
            OnPropertyChanged(nameof(TotalReleasedMb));
        }
    }

    private void SaveSettings()
    {
        var settings = _scheduler.Settings;
        settings.Enabled = Enabled;
        settings.Mode = Mode;
        settings.IntervalMinutes = Math.Max(1, IntervalMinutes);
        settings.CronExpression = CronExpressionText.Trim();
        settings.MemoryThresholdPercent = ThresholdPercent;
        settings.IdleMinutesRequired = IdleMinutes;
        settings.ThresholdTriggerEnabled = ThresholdTrigger;
        settings.PurgeStandbyList = PurgeStandby;
        settings.LowPriorityStandbyOnly = LowPriorityStandby;
        settings.AdjustPriority = AdjustPriority;

        if (Mode == "cron" && !CronExpression.TryParse(settings.CronExpression, out _, out var error))
        {
            StatusText = $"cron 表达式无效：{error}";
            return;
        }

        if (PurgeStandby && !IsElevated)
        {
            StatusText = "已保存。注意：待机列表清理需要管理员权限，当前为普通权限，执行时会被跳过。";
        }

        settings.Save(_host.Database);
        _host.Settings.ResourceAutoStart = Enabled;
        _scheduler.Reload();

        if (Enabled)
        {
            if (!_scheduler.IsRunning) _scheduler.Start();
            StatusText = $"设置已保存并生效：{settings.DescribeSchedule()}";
        }
        else
        {
            _scheduler.Stop();
            StatusText = "设置已保存：自动清理已停用，调度器已停止。";
        }
    }

    private void SaveWhitelist()
    {
        var names = WhitelistInput
            .Split([',', '，', ';', '；', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _optimizer.SaveWhitelist(names);
        StatusText = names.Count == 0
            ? "已恢复为默认白名单（未添加自定义进程）。"
            : $"已保存自定义白名单：{string.Join("、", names)}";
    }
}
