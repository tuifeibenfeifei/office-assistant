using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.App.ViewModels;
using OfficeAssistant.App.Views;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App;

/// <summary>
/// 主窗口：系统托盘常驻 + 界面导航宿主。
///
/// 按文档第三章的界面规格，窗口本身只做两件事：铺深蓝渐变背景、承载当前界面。
/// 界面之间是"主界面 → 功能页 → 返回"的层级导航，切换时 200ms 淡入淡出（文档 3.3）。
/// 关闭按钮默认最小化到托盘（可在设置中改为直接退出）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<AppScreen, FrameworkElement> _screens = [];
    private TrayService? _tray;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _viewModel = new MainViewModel(host);
        DataContext = _viewModel;

        _viewModel.Notify = (title, message) => _tray?.ShowNotification(title, message);
        _viewModel.UpdateBadge = count => _tray?.SetBadge(count);
        _viewModel.ScreenChangeRequested += ShowScreen;

        WireDialogServices();
        Loaded += OnLoaded;
    }

    public void AttachTray(TrayService tray) => _tray = tray;

    /// <summary>主窗口的数据上下文（自检与调试用）。</summary>
    public MainViewModel ViewModel => _viewModel;

    /// <summary>当前显示的界面内容（自检用于确认界面确实切换了）。</summary>
    public FrameworkElement? ScreenContent => ScreenHost.Content as FrameworkElement;

    /// <summary>
    /// 首次加载时那次完整初始化的任务（启动取证与调试用）。
    /// 自检模式下不启动初始化，此属性保持为 null。
    /// </summary>
    public Task? InitializationTask { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _tray?.SetTooltip($"{_viewModel.ProductName} 正在运行");

        // 起始界面永远是主界面
        ShowScreen(AppScreen.Home);

        // 自检模式由 App 显式驱动初始化，避免与 Loaded 中的异步初始化重复执行
        if (App.SmokeTestMode) return;

        InitializationTask = _viewModel.InitializeAsync();
        try
        {
            await InitializationTask.ConfigureAwait(true);
        }
        finally
        {
            UpdateTrayTooltip();
        }
    }

    private void UpdateTrayTooltip()
        => _tray?.SetTooltip($"{_viewModel.ProductName} · 内存 {_viewModel.Memory.MemoryText.Ellipsis(48)}");

    // ------------------------------------------------------------------ 界面导航

    /// <summary>
    /// 切换界面。界面实例按需创建并缓存——缓存既避免每次切换重新解析 XAML，
    /// 也满足文档 3.3"返回时保持原状态（不重置画布位置与展开层级）"的要求。
    /// </summary>
    private void ShowScreen(AppScreen screen)
    {
        var view = GetOrCreateScreen(screen);

        ScreenHost.Content = view;

        // 3.3：页面切换统一 200ms 淡入
        view.Opacity = 0;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private FrameworkElement GetOrCreateScreen(AppScreen screen)
    {
        if (_screens.TryGetValue(screen, out var cached)) return cached;

        FrameworkElement created = screen switch
        {
            AppScreen.Home => CreateHomeScreen(),
            AppScreen.Network => CreateNetworkScreen(),
            AppScreen.Disk => CreateDiskScreen(),
            AppScreen.Memory => CreateMemoryScreen(),
            AppScreen.Settings => CreateSettingsScreen(),
            _ => CreateHomeScreen(),
        };

        _screens[screen] = created;
        return created;
    }

    private FrameworkElement CreateHomeScreen()
    {
        var view = new HomeScreen { DataContext = _viewModel.Home };
        view.SettingsRequested += () => _viewModel.Navigate(AppScreen.Settings);
        view.NetworkRequested += () => _viewModel.Navigate(AppScreen.Network);
        view.DiskRequested += () => _viewModel.Navigate(AppScreen.Disk);
        view.MemoryRequested += () => _viewModel.Navigate(AppScreen.Memory);
        return view;
    }

    private FrameworkElement CreateNetworkScreen()
    {
        var view = new NetworkScreen { DataContext = _viewModel.Network };
        view.BackRequested += _viewModel.GoHome;
        return view;
    }

    private FrameworkElement CreateMemoryScreen()
    {
        var view = new MemoryScreen { DataContext = _viewModel.Memory };
        view.BackRequested += _viewModel.GoHome;
        return view;
    }

    /// <summary>磁盘管理：主路径是目录树 + 批量操作，画布（磁盘全貌）作为二级视图内嵌。</summary>
    private FrameworkElement CreateDiskScreen()
    {
        var view = new DiskTreeScreen { DataContext = _viewModel.Disk };
        view.BackRequested += _viewModel.GoHome;
        return view;
    }

    private FrameworkElement CreateSettingsScreen()
    {
        var view = new SettingsScreen { DataContext = _viewModel.Settings };
        view.BackRequested += _viewModel.GoHome;
        return view;
    }

    /// <summary>把窗口级对话框能力注入各界面视图模型（界面本身不直接引用 MessageBox）。</summary>
    private void WireDialogServices()
    {
        bool Confirm(string title, string message) => Dialogs.Confirm(this, title, message);

        _viewModel.Disk.RequestNotify = message => _tray?.ShowNotification("磁盘清理", message);
        _viewModel.Disk.RequestDeleteAsync = RequestDeleteAsync;
        _viewModel.Disk.RequestTreeReload = () => _ = _viewModel.Disk.RefreshTreeAsync();

        _viewModel.Network.RequestConfirm = Confirm;
        _viewModel.Network.ShowResultDialog = (title, text) => Dialogs.ShowText(this, title, text);

        _viewModel.Memory.RequestConfirm = Confirm;

        _viewModel.Settings.RequestConfirm = Confirm;
        _viewModel.Settings.ShowInfo = (title, text) => Dialogs.ShowText(this, title, text);
        // 自动清理开关直接影响托盘图标形态，切换后立即同步
        _viewModel.Settings.AutoCleanupChanged = enabled =>
            _tray?.SetIcon(IconFactory.CreateAppIcon(0, enabled));
    }

    /// <summary>
    /// 删除流程：误删防护 → 二次确认 → （危险级/大批量）输入确认词 → 回收站。
    /// 全部走 OperationExecutor，保证写入操作日志与审计日志。
    /// </summary>
    private async Task<bool> RequestDeleteAsync(IReadOnlyList<string> paths, string caption)
    {
        if (paths.Count == 0) return false;

        // 误删防护预检：CheckDeletable 属于 OperationExecutor（与真正执行删除的是同一套判定）
        var blocked = paths.Select(p => (_host.OperationExecutor.CheckDeletable(p), p)).Where(t => t.Item1 is not null).ToList();
        var allowed = paths.Where(p => _host.OperationExecutor.CheckDeletable(p) is null).ToList();

        if (allowed.Count == 0)
        {
            Dialogs.ShowText(this, "已被误删防护拦截",
                $"共 {paths.Count} 个条目全部命中保护规则，未执行任何操作。\n\n首个原因：{blocked[0].Item1}");
            return false;
        }

        long size = 0;
        foreach (var path in allowed)
        {
            try
            {
                if (File.Exists(path)) size += new FileInfo(path).Length;
            }
            catch
            {
                // 忽略单个文件
            }
        }

        var message = $"{caption}\n\n" +
                      $"待处理 {allowed.Count} 个条目，合计约 {Format.Bytes(size)}。\n" +
                      "删除将移入回收站（可恢复），并记录到操作日志。\n";
        if (blocked.Count > 0)
            message += $"\n其中 {blocked.Count} 个条目因保护规则被自动排除：\n  · {string.Join("\n  · ", blocked.Take(5).Select(b => PathTools.Shorten(b.Item2, 60) + " —— " + b.Item1))}\n";

        message += "\n是否继续？";
        if (!Dialogs.Confirm(this, caption, message)) return false;

        // 高危操作（数量大或包含用户文档目录）需输入确认词
        var needsTypedConfirm = allowed.Count >= 20 || allowed.Any(ProtectionEngine.IsUserDocumentArea);
        if (needsTypedConfirm)
        {
            var typed = Dialogs.PromptForText(this, "高危操作确认",
                $"本次将处理 {allowed.Count} 个条目（含用户文档目录内容或数量较多）。\n" +
                $"请输入「{AppSettings.DangerConfirmWord}」两个字以确认执行：");
            if (!string.Equals(typed?.Trim(), AppSettings.DangerConfirmWord, StringComparison.Ordinal))
            {
                Dialogs.ShowText(this, "已取消", "确认词不匹配，操作已取消，未删除任何文件。");
                return false;
            }
        }

        var result = await Task.Run(() => _host.OperationExecutor.Recycle(allowed, caption)).ConfigureAwait(true);
        if (result.Ok)
        {
            _tray?.ShowNotification("删除完成", result.Message);
            _viewModel.StatusText = result.Message;
            return true;
        }

        Dialogs.ShowText(this, "删除未完成", result.Message);
        _viewModel.StatusText = result.Message;
        return false;
    }

    /// <summary>从托盘恢复显示。</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>托盘菜单"使用说明"：唤起窗口并直接落到设置的使用说明页（文档 3.6）。</summary>
    public void ShowHelpFromTray()
    {
        ShowFromTray();
        _viewModel.Navigate(AppScreen.Settings);
        if (_screens[AppScreen.Settings] is SettingsScreen settings) settings.ShowHelpPage();
    }

    private bool _shutdownPrepared;

    /// <summary>
    /// 收尾：释放视图模型与宿主。幂等——自检模式、托盘"退出"与点关闭三条路径都可能调用。
    ///
    /// 注意：本方法会先把 <see cref="_shutdownPrepared"/> 置位，因此**在调用 Close() 之前**调用它，
    /// 可以保证窗口的后续（可能重复的）关闭流程不再去读已释放的宿主。
    /// </summary>
    public void PrepareShutdown()
    {
        if (_shutdownPrepared) return;
        _shutdownPrepared = true;
        try { _viewModel.Dispose(); } catch { /* 忽略 */ }
        try { _host.Dispose(); } catch { /* 忽略 */ }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 已经走过收尾流程（自检/取证模式、托盘"退出"、或用户选择直接退出）时不再做任何判断：
        // 此时宿主可能已释放，读设置会失败并留下无意义的告警。
        if (_shutdownPrepared || App.SmokeTestMode)
        {
            PrepareShutdown();
            return;
        }

        bool minimizeToTray;
        try
        {
            minimizeToTray = _host.Settings.MinimizeToTray;
        }
        catch (Exception ex)
        {
            // 宿主不可用时按"直接退出"处理：关闭窗口不该因为读设置失败而卡住
            Log.Warn($"读取托盘设置失败，按直接退出处理：{ex.Message}");
            minimizeToTray = false;
        }

        if (minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowNotification($"{_viewModel.ProductName} 仍在后台运行",
                "已最小化到系统托盘：磁盘扫描与内存整理都会继续按配置工作。双击托盘图标可重新打开。");
            return;
        }

        PrepareShutdown();
    }

    /// <summary>Esc：在功能页返回主界面，在主界面则最小化到托盘。</summary>
    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_viewModel.CurrentScreen != AppScreen.Home)
        {
            _viewModel.GoHome();
            e.Handled = true;
            return;
        }

        Hide();
    }
}

/// <summary>字符串截断辅助。</summary>
internal static class StringExtensions
{
    public static string Ellipsis(this string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
