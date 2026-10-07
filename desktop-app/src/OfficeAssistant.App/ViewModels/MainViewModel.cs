using System.Windows.Threading;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>当前显示的界面（文档 3.4 的五个界面）。</summary>
public enum AppScreen
{
    /// <summary>界面 1：主界面（极简入口）。</summary>
    Home,

    /// <summary>界面 2：网络加速。</summary>
    Network,

    /// <summary>界面 3：磁盘清理（画布式）。</summary>
    Disk,

    /// <summary>界面 4：内存清理。</summary>
    Memory,

    /// <summary>界面 5：设置。</summary>
    Settings,
}

/// <summary>
/// 主窗口视图模型：持有各界面视图模型并管理界面切换。
///
/// 按文档第三章，界面之间是"主界面 → 功能页 → 返回"的层级导航，
/// 不再是并列页签；因此这里只保留一个当前界面状态与前进/返回两个动作。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;

    private AppScreen _currentScreen = AppScreen.Home;
    private string _statusText = "就绪";
    private string _badgeText = string.Empty;
    private string _indexText = "索引为空";

    public MainViewModel(AppHost host)
    {
        _host = host;
        Home = new HomeViewModel(host);
        Network = new NetworkPageViewModel(host);
        Disk = new DiskPageViewModel(host);
        Memory = new ResourcePageViewModel(host);
        Settings = new SettingsPageViewModel(host);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => RefreshCounters();
    }

    public HomeViewModel Home { get; }

    public NetworkPageViewModel Network { get; }

    public DiskPageViewModel Disk { get; }

    public ResourcePageViewModel Memory { get; }

    public SettingsPageViewModel Settings { get; }

    /// <summary>由窗口注入：弹出系统托盘通知。</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>由窗口注入：更新托盘角标。</summary>
    public Action<int>? UpdateBadge { get; set; }

    /// <summary>请求窗口执行界面切换（由窗口订阅，负责实际换页与过渡动效）。</summary>
    public event Action<AppScreen>? ScreenChangeRequested;

    public string ProductName => $"{AppPaths.ProductName} v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string BadgeText
    {
        get => _badgeText;
        private set => SetProperty(ref _badgeText, value);
    }

    public string IndexText
    {
        get => _indexText;
        private set => SetProperty(ref _indexText, value);
    }

    public AppScreen CurrentScreen
    {
        get => _currentScreen;
        private set
        {
            if (!SetProperty(ref _currentScreen, value)) return;
            OnPropertyChanged(nameof(IsHome));
        }
    }

    /// <summary>当前是否在主界面（供窗口决定是否显示返回箭头等外壳元素）。</summary>
    public bool IsHome => CurrentScreen == AppScreen.Home;

    public bool IsElevated => _host.IsElevated;

    public string ElevationHint => IsElevated
        ? "管理员权限"
        : "普通权限（删除/清理仍可用；待机列表清理需管理员）";

    /// <summary>切换到指定界面。</summary>
    public void Navigate(AppScreen screen)
    {
        CurrentScreen = screen;
        if (screen == AppScreen.Home) Home.Refresh(Network);
        ScreenChangeRequested?.Invoke(screen);
    }

    /// <summary>返回主界面。</summary>
    public void GoHome() => Navigate(AppScreen.Home);

    public async Task InitializeAsync()
    {
        StatusText = "正在初始化各功能模块…";
        try
        {
            await Task.WhenAll(
                Network.InitializeAsync(),
                Disk.InitializeAsync(),
                Memory.InitializeAsync(),
                Settings.InitializeAsync()).ConfigureAwait(true);

            _host.StartBackgroundServices();
            RefreshCounters();
            Home.Refresh(Network);
            _statusTimer.Start();
            StatusText = "就绪。所有数据仅存本机；删除统一进回收站，可恢复。";
        }
        catch (Exception ex)
        {
            Log.Error("初始化失败", ex);
            StatusText = $"初始化未完全成功：{ex.Message}";
        }
    }

    private void RefreshCounters()
    {
        try
        {
            var files = _host.Database.CountFiles();
            IndexText = files == 0
                ? "索引为空（尚未扫描磁盘）"
                : $"索引 {files:N0} 个文件 / {Format.Bytes(_host.Database.TotalBytes())}，未识别 {_host.Database.CountUnrecognized():N0} 个" +
                  $"；调度：{_host.ResourceScheduler.Settings.DescribeSchedule()}";

            BadgeText = string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug($"刷新计数失败：{ex.Message}");
        }
    }

    public void Dispose() => _statusTimer.Stop();
}
