using System.Collections.ObjectModel;
using System.Diagnostics;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Compliance;
using OfficeAssistant.Core.Homework;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Scanning;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>卷选择行（扫描范围配置）。</summary>
public sealed class ScanRootViewModel : ObservableObject
{
    private bool _selected;

    public ScanRootViewModel(VolumeInfo info, bool selected)
    {
        Info = info;
        _selected = selected;
    }

    public VolumeInfo Info { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Root => Info.Root;
    public string Label => string.IsNullOrWhiteSpace(Info.Label) ? Info.DriveType : Info.Label;
    public string CapacityText => $"{Format.Bytes(Info.UsedBytes)} 已用 / {Format.Bytes(Info.TotalBytes)}" +
                                  (Info.Scannable ? string.Empty : "（" + Info.DriveType + "，不可扫描）");
}

/// <summary>
/// 设置页视图模型：扫描范围、托盘行为、平台适配器端点、数据与隐私、合规自检、日志查看。
/// </summary>
public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly AppHost _host;

    private bool _onlySystemDrive;
    private int _scanParallelism;
    private bool _scanWithRecognition = true;
    private bool _useIncremental = true;
    private bool _minimizeToTray = true;
    private bool _showBadge = true;
    private bool _resourceAutoStart;
    private bool _autoStart;
    private bool _homeworkAutoSync;
    private bool _isBusy;
    private string _statusText = "所有设置仅保存在本机 SQLite 库中，不会上传。";
    private string _auditText = "尚未运行合规自检。";
    private string _logText = string.Empty;
    private string _adapterBaseUrl = string.Empty;
    private string _adapterLoginPath = "/api/login";
    private string _adapterCoursePath = "/api/courses";
    private string _adapterHomeworkPath = "/api/homework";
    private string _adapterAccountField = "phone";
    private string _adapterPasswordField = "password";
    private string _adapterCourseArray = "data";
    private string _adapterHomeworkArray = "data";
    private string _adapterIdField = "id";
    private string _adapterTitleField = "title";
    private string _adapterCourseIdField = "courseId";
    private string _adapterDeadlineField = "deadline";
    private string _adapterStatusField = "status";

    public SettingsPageViewModel(AppHost host)
    {
        _host = host;
        _onlySystemDrive = host.Settings.OnlySystemDrive;
        _scanParallelism = host.Settings.ScanParallelism;
        _scanWithRecognition = host.Settings.ScanWithRecognition;
        _useIncremental = host.Settings.IncrementalScan;
        _minimizeToTray = host.Settings.MinimizeToTray;
        _showBadge = host.Settings.ShowTrayBadge;
        _resourceAutoStart = host.Settings.ResourceAutoStart;
        _autoStart = host.Settings.AutoStart;
        _homeworkAutoSync = host.Settings.HomeworkAutoSyncEnabled;

        var config = host.Homework.AdapterConfig;
        _adapterBaseUrl = config.BaseUrl;
        _adapterLoginPath = config.LoginPath;
        _adapterCoursePath = config.CoursePath;
        _adapterHomeworkPath = config.HomeworkPath;
        _adapterAccountField = config.AccountField;
        _adapterPasswordField = config.PasswordField;
        _adapterCourseArray = config.CourseArrayPath;
        _adapterHomeworkArray = config.HomeworkArrayPath;
        _adapterIdField = config.HomeworkIdField;
        _adapterTitleField = config.HomeworkTitleField;
        _adapterCourseIdField = config.HomeworkCourseIdField;
        _adapterDeadlineField = config.HomeworkDeadlineField;
        _adapterStatusField = config.HomeworkStatusField;

        SaveCommand = new RelayCommand(Save);
        RunAuditCommand = new AsyncRelayCommand(RunAuditAsync, () => !IsBusy);
        LoadLogCommand = new AsyncRelayCommand(LoadLogAsync, () => !IsBusy);
        ClearDataCommand = new AsyncRelayCommand(ClearDataAsync, () => !IsBusy);
        ClearCredentialCommand = new RelayCommand(ClearCredential);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.DataRoot));
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.LogDirectory));
        SaveAdapterCommand = new RelayCommand(SaveAdapter);
        ResetAdapterCommand = new RelayCommand(ResetAdapter);
        ReloadVolumesCommand = new AsyncRelayCommand(LoadVolumesAsync, () => !IsBusy);
    }

    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand RunAuditCommand { get; }
    public AsyncRelayCommand LoadLogCommand { get; }
    public AsyncRelayCommand ClearDataCommand { get; }
    public RelayCommand ClearCredentialCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand SaveAdapterCommand { get; }
    public RelayCommand ResetAdapterCommand { get; }
    public AsyncRelayCommand ReloadVolumesCommand { get; }

    public Func<string, string, bool>? RequestConfirm { get; set; }
    public Action<string, string>? ShowInfo { get; set; }

    public ObservableCollection<ScanRootViewModel> Volumes { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RunAuditCommand.RaiseCanExecuteChanged();
            LoadLogCommand.RaiseCanExecuteChanged();
            ClearDataCommand.RaiseCanExecuteChanged();
            ReloadVolumesCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string AuditText
    {
        get => _auditText;
        private set => SetProperty(ref _auditText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string DataPathText => $"数据目录：{AppPaths.DataRoot}";

    public string DatabaseText => $"本地数据库：{AppPaths.DatabaseFile}（SQLite {_host.Database.EngineVersion}）";

    public string AuditLogText => $"操作审计日志：{AppPaths.AuditLogFile}";

    public string CredentialText => _host.Homework.Vault.HasCredential
        ? $"学习通凭据：已保存（DPAPI 加密，账户 {CredentialVault.Mask(_host.Homework.Vault.SavedAccount ?? string.Empty)}）"
        : "学习通凭据：未保存";

    public string IndexText =>
        $"索引：文件 {_host.Database.CountFiles():N0} 个，目录 {_host.Database.CountDirs():N0} 个，" +
        $"未识别 {_host.Database.CountUnrecognized():N0} 个，目录指纹 {_host.Database.CountDirIndex():N0} 条";

    public string VersionText
    {
        get
        {
            var version = typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            return $"全能办公助手 v{version} · {Environment.OSVersion.VersionString} · " +
                   $"{(_host.IsElevated ? "管理员权限" : "普通权限")} · WebView2/独立进程";
        }
    }

    public bool OnlySystemDrive
    {
        get => _onlySystemDrive;
        set => SetProperty(ref _onlySystemDrive, value);
    }

    public int ScanParallelism
    {
        get => _scanParallelism;
        set => SetProperty(ref _scanParallelism, Math.Clamp(value, 1, 32));
    }

    public bool ScanWithRecognition
    {
        get => _scanWithRecognition;
        set => SetProperty(ref _scanWithRecognition, value);
    }

    public bool UseIncremental
    {
        get => _useIncremental;
        set => SetProperty(ref _useIncremental, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool ShowBadge
    {
        get => _showBadge;
        set => SetProperty(ref _showBadge, value);
    }

    /// <summary>
    /// 自动清理内存（文档 3.4 界面 5"常规"三项开关之一）。
    /// 对应资源管家的开机自启：开启后调度器随程序启动并按调度执行清理。
    /// </summary>
    public bool Enabled
    {
        get => _resourceAutoStart;
        set
        {
            if (_resourceAutoStart == value) return;
            try
            {
                _host.Settings.ResourceAutoStart = value;
                SetProperty(ref _resourceAutoStart, value);
                if (value) _host.ResourceScheduler.Start();
                StatusText = value ? "自动清理内存已开启。" : "自动清理内存已关闭。";
                AutoCleanupChanged?.Invoke(value);
            }
            catch (Exception ex)
            {
                StatusText = $"自动清理设置失败：{ex.Message}";
            }
        }
    }

    /// <summary>开关变化通知（窗口据此更新托盘图标状态）。</summary>
    public Action<bool>? AutoCleanupChanged { get; set; }

    public bool ResourceAutoStart
    {
        get => _resourceAutoStart;
        set => SetProperty(ref _resourceAutoStart, value);
    }

    /// <summary>
    /// 开机启动（文档 3.4 界面 5"常规"三项开关之一）。
    /// 立即写入注册表启动项：设置类开关不应要求用户再点一次"保存"，
    /// 否则用户以为已生效、实际没有。
    /// </summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_autoStart == value) return;
            try
            {
                _host.Settings.AutoStart = value;
                SetProperty(ref _autoStart, value);
                StatusText = value ? "已设置为开机启动。" : "已取消开机启动。";
            }
            catch (Exception ex)
            {
                // 写注册表失败（安全软件/组策略）时回读真实状态，避免开关显示与事实不符
                _autoStart = _host.Settings.AutoStart;
                OnPropertyChanged(nameof(AutoStart));
                StatusText = $"开机启动设置失败：{ex.Message}";
            }
        }
    }

    public bool HomeworkAutoSync
    {
        get => _homeworkAutoSync;
        set => SetProperty(ref _homeworkAutoSync, value);
    }

    public string AdapterBaseUrl { get => _adapterBaseUrl; set => SetProperty(ref _adapterBaseUrl, value); }
    public string AdapterLoginPath { get => _adapterLoginPath; set => SetProperty(ref _adapterLoginPath, value); }
    public string AdapterCoursePath { get => _adapterCoursePath; set => SetProperty(ref _adapterCoursePath, value); }
    public string AdapterHomeworkPath { get => _adapterHomeworkPath; set => SetProperty(ref _adapterHomeworkPath, value); }
    public string AdapterAccountField { get => _adapterAccountField; set => SetProperty(ref _adapterAccountField, value); }
    public string AdapterPasswordField { get => _adapterPasswordField; set => SetProperty(ref _adapterPasswordField, value); }
    public string AdapterCourseArray { get => _adapterCourseArray; set => SetProperty(ref _adapterCourseArray, value); }
    public string AdapterHomeworkArray { get => _adapterHomeworkArray; set => SetProperty(ref _adapterHomeworkArray, value); }
    public string AdapterIdField { get => _adapterIdField; set => SetProperty(ref _adapterIdField, value); }
    public string AdapterTitleField { get => _adapterTitleField; set => SetProperty(ref _adapterTitleField, value); }
    public string AdapterCourseIdField { get => _adapterCourseIdField; set => SetProperty(ref _adapterCourseIdField, value); }
    public string AdapterDeadlineField { get => _adapterDeadlineField; set => SetProperty(ref _adapterDeadlineField, value); }
    public string AdapterStatusField { get => _adapterStatusField; set => SetProperty(ref _adapterStatusField, value); }

    public string AdapterHint =>
        "学习通未提供公开开放接口。此处的端点与字段映射需由你按自己的抓包结果填写；" +
        "未填写时同步会明确降级为「本地/导入模式」，不会返回任何伪造数据。" +
        "请求频率受控（默认不低于每 10 分钟一次，失败按指数退避），会话失效时提示重新登录而不静默重试。";

    public async Task InitializeAsync()
    {
        await LoadVolumesAsync().ConfigureAwait(true);
        await RunAuditAsync().ConfigureAwait(true);
        await LoadLogAsync().ConfigureAwait(true);
    }

    private async Task LoadVolumesAsync()
    {
        var volumes = await Task.Run(ScanEngine.ListVolumes).ConfigureAwait(true);
        var saved = _host.Settings.ScanRoots;
        Volumes.Clear();
        foreach (var volume in volumes)
        {
            var selected = saved.Count == 0 ? volume.Scannable : saved.Contains(volume.Root, StringComparer.OrdinalIgnoreCase);
            Volumes.Add(new ScanRootViewModel(volume, selected && volume.Scannable));
        }
    }

    private void Save()
    {
        _host.Settings.OnlySystemDrive = OnlySystemDrive;
        _host.Settings.ScanParallelism = ScanParallelism;
        _host.Settings.ScanWithRecognition = ScanWithRecognition;
        _host.Settings.IncrementalScan = UseIncremental;
        _host.Settings.MinimizeToTray = MinimizeToTray;
        _host.Settings.ShowTrayBadge = ShowBadge;
        _host.Settings.ResourceAutoStart = ResourceAutoStart;
        _host.Settings.HomeworkAutoSyncEnabled = HomeworkAutoSync;
        _host.Settings.ScanRoots = Volumes.Where(v => v.Selected && v.Info.Scannable).Select(v => v.Root).ToList();

        StatusText = Volumes.Count(v => v.Selected && v.Info.Scannable) == 0
            ? "设置已保存。当前未勾选任何卷，扫描时将默认覆盖全部固定磁盘。"
            : "设置已保存并立即生效。";
    }

    private async Task RunAuditAsync()
    {
        IsBusy = true;
        try
        {
            var report = await Task.Run(_host.RunComplianceAudit).ConfigureAwait(true);
            AuditText = ComplianceAudit.RenderReport(report);
            StatusText = report.Summary;
        }
        catch (Exception ex)
        {
            AuditText = $"合规自检执行失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadLogAsync()
    {
        LogText = await Task.Run(() => Log.Tail(400)).ConfigureAwait(true);
    }

    private async Task ClearDataAsync()
    {
        var confirmed = RequestConfirm?.Invoke("清除本地数据",
            "将删除本机保存的以下数据：\n" +
            "  · 扫描索引与目录指纹\n  · 课程与作业数据、同步日志、提醒记录\n  · 操作日志、设置与清理规则\n  · 学习通凭据（DPAPI 密文）\n\n" +
            "此操作不可撤销，但不会影响磁盘上的任何文件。是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                _host.Homework.Vault.Clear();
                _host.Database.PurgeAllUserData();
            }).ConfigureAwait(true);
            StatusText = "本地数据已清除。建议重启程序以重建默认规则。";
            ShowInfo?.Invoke("已清除", "本地数据与凭据已清除。磁盘文件未受任何影响。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearCredential()
    {
        var confirmed = RequestConfirm?.Invoke("清除学习通凭据",
            "将立即删除本机保存的学习通凭据密文文件。是否继续？") ?? false;
        if (!confirmed) return;
        _host.Homework.Vault.Clear();
        StatusText = "学习通凭据已清除。";
        OnPropertyChanged(nameof(CredentialText));
    }

    private void SaveAdapter()
    {
        var config = _host.Homework.AdapterConfig;
        config.BaseUrl = AdapterBaseUrl.Trim();
        config.LoginPath = AdapterLoginPath.Trim();
        config.CoursePath = AdapterCoursePath.Trim();
        config.HomeworkPath = AdapterHomeworkPath.Trim();
        config.AccountField = AdapterAccountField.Trim();
        config.PasswordField = AdapterPasswordField.Trim();
        config.CourseArrayPath = AdapterCourseArray.Trim();
        config.HomeworkArrayPath = AdapterHomeworkArray.Trim();
        config.HomeworkIdField = AdapterIdField.Trim();
        config.HomeworkTitleField = AdapterTitleField.Trim();
        config.HomeworkCourseIdField = AdapterCourseIdField.Trim();
        config.HomeworkDeadlineField = AdapterDeadlineField.Trim();
        config.HomeworkStatusField = AdapterStatusField.Trim();

        if (!string.IsNullOrWhiteSpace(config.BaseUrl) &&
            (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            StatusText = "平台地址格式无效，应以 http:// 或 https:// 开头。";
            return;
        }

        config.Save(_host.Database);
        StatusText = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "已清空平台端点，学习通页将保持「本地/导入模式」。"
            : $"平台端点已保存：{config.BaseUrl}。重启程序后生效（适配器在启动时装配）。";
    }

    private void ResetAdapter()
    {
        var confirmed = RequestConfirm?.Invoke("重置适配器配置",
            "将清空平台端点与字段映射，学习通页回到「本地/导入模式」。是否继续？") ?? false;
        if (!confirmed) return;

        var config = _host.Homework.AdapterConfig;
        var empty = new AdapterConfig();
        config.BaseUrl = empty.BaseUrl;
        config.LoginPath = empty.LoginPath;
        config.CoursePath = empty.CoursePath;
        config.HomeworkPath = empty.HomeworkPath;
        config.AccountField = empty.AccountField;
        config.PasswordField = empty.PasswordField;
        config.Save(_host.Database);

        AdapterBaseUrl = string.Empty;
        AdapterLoginPath = empty.LoginPath;
        AdapterCoursePath = empty.CoursePath;
        AdapterHomeworkPath = empty.HomeworkPath;
        AdapterAccountField = empty.AccountField;
        AdapterPasswordField = empty.PasswordField;
        StatusText = "适配器配置已重置。";
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败：{ex.Message}");
        }
    }
}
