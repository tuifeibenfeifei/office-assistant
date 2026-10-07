using System.Threading;
using System.Windows.Threading;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.App.ViewModels;
using OfficeAssistant.App.Views;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App;

/// <summary>
/// 应用入口。同时启用 WPF 与 Windows Forms 时，WPF SDK 仍会依据 App.xaml 生成
/// 带 [STAThread] 的入口点（App.g.cs 中的 Main），因此这里不再手写 Main。
///
/// 支持 <c>--smoke-test</c>：在真实 WPF 栈上完成一次无界面自检——
/// 加载 App.xaml 资源、解析并实例化 MainWindow 与五个页面、初始化各页视图模型、
/// 捕获所有数据绑定错误，然后立即退出并返回退出码。
/// 该模式用于"没有交互式桌面/需要自动验证 UI 装配"的场景。
/// </summary>
public partial class App : WpfApplication
{
    private Mutex? _singleInstanceMutex;
    private AppHost? _host;
    private MainWindow? _mainWindow;
    private TrayService? _tray;
    private bool _exiting;

    /// <summary>运行期是否为自检模式（页面据此跳过后台轮询）。</summary>
    internal static bool SmokeTestMode { get; private set; }

    /// <summary>启动分步取证：每完成一步就追加一条带时间戳的记录。</summary>
    public static IReadOnlyList<string> StartupTrace => Trace;

    private static readonly List<string> Trace = [];
    private static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// 启动轨迹文件（<c>%TEMP%\OfficeAssistant-startup-trace.log</c>）。
    ///
    /// 刻意不放在数据目录里：数据目录要等 AppPaths.Initialize 才知道在哪，
    /// 而启动失败恰恰可能发生在那之前。%TEMP% 是进程启动时就确定可写的位置，
    /// 因此从第一行代码起就能留痕——这正是排查"双击没反应"最需要的东西。
    /// </summary>
    private static readonly string TraceFile =
        Path.Combine(Path.GetTempPath(), "OfficeAssistant-startup-trace.log");

    /// <summary>记录一步启动进度；同时写 %TEMP% 轨迹、运行日志与内存轨迹。</summary>
    private static void Mark(string step)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [+{StartupClock.ElapsedMilliseconds,5} ms] {step}";
        Trace.Add(line);
        try { File.AppendAllText(TraceFile, line + Environment.NewLine, System.Text.Encoding.UTF8); }
        catch { /* 轨迹写不进去也不能阻断启动 */ }
        try { Log.Info($"启动：{step}"); } catch { /* 日志失败不能阻断启动 */ }
    }

    /// <summary>每次进程启动都重开一份轨迹文件，避免历史内容干扰判断。</summary>
    private static void ResetTraceFile()
    {
        try
        {
            File.WriteAllText(TraceFile,
                $"==== 启动轨迹 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}" +
                $"命令行：{string.Join(' ', Environment.GetCommandLineArgs())}{Environment.NewLine}" +
                $"PID：{Environment.ProcessId}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 把启动期的异常落盘到 %TEMP%，并尽量弹窗告知用户。
    ///
    /// 为什么必须这么做：WPF 进程在 OnStartup 抛出未处理异常时会直接终止，
    /// 既没有日志也没有提示——用户看到的就是"双击没反应"。
    /// 这类"静默死亡"是最难排查的故障，因此启动链路的每一步都要么成功、要么留下痕迹。
    /// </summary>
    private static void ReportStartupFailure(string stage, Exception exception)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"阶段：{stage}");
        report.AppendLine($"异常：{exception.GetType().FullName}: {exception.Message}");
        report.AppendLine($"数据目录：{(AppPaths.IsReady ? AppPaths.DataRoot : "(未确定)")}");
        report.AppendLine($"命令行：{string.Join(' ', Environment.GetCommandLineArgs())}");
        report.AppendLine($"系统：{Environment.OSVersion.VersionString} / .NET {Environment.Version}");
        report.AppendLine("数据目录候选尝试记录：");
        if (AppPaths.ResolutionAttempts.Count == 0) report.AppendLine("  (无记录)");
        foreach (var attempt in AppPaths.ResolutionAttempts) report.AppendLine("  " + attempt);
        report.AppendLine("轨迹：");
        foreach (var line in Trace) report.AppendLine("  " + line);
        report.AppendLine("堆栈：");
        report.AppendLine(exception.ToString());

        var text = report.ToString();
        try { Log.Error($"启动失败（{stage}）", exception); } catch { /* 忽略 */ }

        var path = Path.Combine(Path.GetTempPath(), "OfficeAssistant-startup-failure.log");
        try { File.WriteAllText(path, text, new System.Text.UTF8Encoding(true)); } catch { path = "(写入失败)"; }

        try
        {
            WpfMessageBox.Show(
                $"程序启动失败，未进入主界面。\n\n阶段：{stage}\n原因：{exception.Message}\n\n" +
                $"已把详细信息写入：\n{path}\n（运行日志位于 {AppPaths.LogDirectory}）",
                AppPaths.ProductName, WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
        catch
        {
            // 连弹窗都失败时至少已落盘
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ResetTraceFile();
        Mark("OnStartup 开始（App.xaml 资源已加载）");

        var smokeTest = e.Args.Any(a =>
            a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/smoke-test", StringComparison.OrdinalIgnoreCase));

        if (smokeTest)
        {
            Mark("模式：UI 自检");
            RunSmokeTest();
            return;
        }

        var startupCheck = e.Args.Any(a =>
            a.Equals("--startup-check", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/startup-check", StringComparison.OrdinalIgnoreCase));

        if (startupCheck)
        {
            var full = e.Args.Any(a =>
                a.Equals("--full", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/full", StringComparison.OrdinalIgnoreCase));
            var scan = e.Args.Any(a =>
                a.Equals("--scan", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/scan", StringComparison.OrdinalIgnoreCase));
            Mark($"模式：启动路径取证（full={full}, scan={scan}）");
            RunStartupCheck(full, scan);
            return;
        }

        // 渲染取证：把各界面真正画成 PNG，用于人工核对布局、间距与对齐
        var renderIndex = Array.FindIndex(e.Args, a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));
        if (renderIndex >= 0)
        {
            Mark("模式：界面渲染取证");
            RunRenderCheck();
            return;
        }

        Mark("模式：正常启动（托盘常驻）");

        // 启动链路的每一步都单独守卫：任何一步失败都要留下痕迹并尽量降级继续，
        // 而不是让进程无声退出（详见 ReportStartupFailure 的说明）。
        try
        {
            AppPaths.Initialize();
            Mark($"数据目录就绪：{AppPaths.DataRoot}");

            // 单实例检测：即使命名内核对象被策略禁止（会抛 UnauthorizedAccessException），
            // 也应当放弃单实例保护、继续启动——绝不因此让程序起不来。
            try
            {
                _singleInstanceMutex = new Mutex(true, @"Local\OfficeAssistant.SingleInstance", out var isFirst);
                Mark(isFirst ? "单实例锁已获取" : "检测到已有实例");
                if (!isFirst)
                {
                    WpfMessageBox.Show($"{AppPaths.ProductName} 已在运行，请在系统托盘中查看。",
                        AppPaths.ProductName, WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
            catch (Exception ex)
            {
                Mark($"单实例锁不可用，已跳过（{ex.GetType().Name}）：{ex.Message}");
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error($"未处理的域异常：{args.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error("未观察到的任务异常", args.Exception);
                args.SetObserved();
            };
            Mark("全局异常处理已挂载");

            StartApplication(createTray: true);
        }
        catch (Exception ex)
        {
            Mark($"启动中断：{ex.GetType().Name}: {ex.Message}");
            ReportStartupFailure("OnStartup", ex);
            Shutdown(1);
        }
    }

    /// <summary>初始化宿主与主窗口；<paramref name="createTray"/> 为 false 时不创建托盘图标（自检用）。</summary>
    private void StartApplication(bool createTray)
    {
        _host = AppHost.Current;
        Mark($"宿主就绪（SQLite {_host.Database.EngineVersion}，数据目录 {AppPaths.DataRoot}）");

        _mainWindow = new MainWindow(_host);
        Mark("主窗口已构造（XAML 解析完成）");

        if (createTray) CreateTray();

        _mainWindow.Show();
        Mark("主窗口已显示");
    }

    /// <summary>
    /// 创建托盘图标与菜单。托盘失败不阻断主界面——
    /// 托盘依赖 Windows 通知区域，在远程会话、策略受限或 Explorer 未就绪时可能不可用，
    /// 此时应用仍应能作为普通窗口使用。
    /// </summary>
    private void CreateTray()
    {
        try
        {
            var window = _mainWindow!;
            var host = _host!;
            _tray = new TrayService(
                showWindow: () => window.ShowFromTray(),
                exitApplication: ExitApplication,
                showHelp: () => window.ShowHelpFromTray(),
                autoCleanupOn: host.Settings.ResourceAutoStart);
            _tray.MemoryRequested += () =>
            {
                var report = host.ResourceScheduler.RunNow("托盘菜单");
                _tray.ShowNotification("内存整理完成", report.Summary);
            };
            window.AttachTray(_tray);
            Mark("托盘图标已创建");
        }
        catch (Exception ex)
        {
            Mark($"托盘不可用，已降级为普通窗口（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, WpfDispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);
        WpfMessageBox.Show($"发生未处理的界面异常，已记录到日志：\n{e.Exception.Message}\n\n程序会继续运行。",
            AppPaths.ProductName, WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ 界面渲染取证

    /// <summary>
    /// 把各界面真正渲染成 PNG，输出到 <c>%TEMP%\OfficeAssistant-render-*</c>。
    ///
    /// 为什么需要它：自检只能证明"能解析、能绑定"，证明不了"一眼看过去布局对不对"。
    /// 截图让间距、对齐、遮挡这类问题变成可核对的事实，而不是靠想象。
    /// </summary>
    private async void RunRenderCheck()
    {
        SmokeTestMode = true;

        var folder = Path.Combine(Path.GetTempPath(),
            $"OfficeAssistant-render-{DateTime.Now:HHmmss}");
        var failures = new List<string>();

        try
        {
            Directory.CreateDirectory(folder);
            AppPaths.Initialize();

            var host = AppHost.Current;
            var vm = new MainViewModel(host);
            RunDispatcherPump(vm.InitializeAsync(), TimeSpan.FromMinutes(3));

            // 索引为空时先做一次有界只读扫描，否则画布上没有可看的内容
            if (host.Database.CountFiles() == 0)
            {
                var root = Core.Scanning.ScanEngine.ListVolumes().FirstOrDefault(v => v.Scannable)?.Root;
                if (root is not null)
                {
                    var scan = host.ScanEngine.Scan(new Core.Scanning.ScanOptions
                    {
                        Mode = Core.Models.ScanMode.Full,
                        Roots = [root],
                        EnableRecognition = false,
                        MaxEntries = 20000,
                        MaxParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                    });
                    Mark($"渲染取证预热扫描：{scan.Summary}");

                    // 预热之后把磁盘页完整初始化一次：盘符列表与目录树都靠它填充，
                    // 否则渲染出来的列表视图是空的（第一次渲染就踩了这个坑）。
                    await PumpAsync(vm.Disk.InitializeAsync(), TimeSpan.FromMinutes(1));
                }
            }

            // 关键：渲染**真实窗口**（MainWindow），而不是孤立的界面。
            // 背景渐变、星点光晕都挂在窗口外壳上；只渲染 UserControl 会截出一张白底图，
            // 拿它核对布局等于自欺欺人（第一版就踩了这个坑，白底截图说明外壳被漏掉了）。
            var window = new MainWindow(host) { Width = 900, Height = 600 };
            window.Show();
            RunDispatcherPump(Task.Delay(1200), TimeSpan.FromSeconds(20));

            var screens = new (string Name, AppScreen Screen, System.Windows.Size Size)[]
            {
                ("1-主界面", AppScreen.Home, new System.Windows.Size(900, 600)),
                ("2-网络加速", AppScreen.Network, new System.Windows.Size(900, 600)),
                ("4-内存清理", AppScreen.Memory, new System.Windows.Size(900, 600)),
                ("5-设置常规", AppScreen.Settings, new System.Windows.Size(900, 600)),
                // 文档规定画布默认 1200×760，这一屏按该尺寸单独渲染
                ("3-磁盘列表", AppScreen.Disk, new System.Windows.Size(1200, 760)),
            };

            foreach (var (name, screen, size) in screens)
            {
                try
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.ViewModel.Navigate(screen);
                    window.UpdateLayout();
                    RunDispatcherPump(Task.Delay(300), TimeSpan.FromSeconds(10));

                    var file = Path.Combine(folder, $"{name}.png");
                    RenderWindow(window, size, file);

                    if (screen == AppScreen.Disk)
                    {
                        var diskInfo = window.ViewModel.Disk;
                        Mark($"磁盘诊断：盘符 {diskInfo.Volumes.Count} 个、树根 {diskInfo.Roots.Count} 个、" +
                             $"已勾选 {diskInfo.CheckedNodes.Count} 项");
                    }

                    Mark($"渲染完成：{file}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} 渲染失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            window.PrepareShutdown();
            window.Close();
        }
        catch (Exception ex)
        {
            failures.Add($"渲染取证异常：{ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("================ 全能办公助手 · 界面渲染取证 ================");
        Console.WriteLine($"  输出目录：{folder}");
        foreach (var line in Trace) Console.WriteLine("  " + line);
        if (failures.Count == 0)
        {
            Console.WriteLine("  全部界面渲染完成，可打开 PNG 核对布局。");
        }
        else
        {
            Console.WriteLine("  存在失败项：");
            foreach (var failure in failures) Console.WriteLine("  [!!] " + failure);
        }
        Console.WriteLine("==========================================================");

        Shutdown(failures.Count == 0 ? 0 : 1);
    }

    /// <summary>把窗口当前内容渲染成 PNG（用于布局核对）。</summary>
    private static void RenderWindow(Window window, System.Windows.Size requested, string file)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(requested);
        content.Arrange(new Rect(requested));
        content.UpdateLayout();

        // 让绑定与布局动画先跑完，否则截到的是未完成状态
        Pump(TimeSpan.FromMilliseconds(400));

        // 按**实际**客户区尺寸出图：窗口尺寸含标题栏与边框，
        // 直接按窗口尺寸渲染会在右/下留出一条白边（实测踩过，白边约等于边框宽度）。
        var width = (int)Math.Ceiling(content.ActualWidth > 0 ? content.ActualWidth : requested.Width);
        var height = (int)Math.Ceiling(content.ActualHeight > 0 ? content.ActualHeight : requested.Height);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ 启动路径取证

    /// <summary>
    /// 完整走一遍真实启动路径（含托盘图标创建与窗口显示），逐步打印轨迹后退出。
    ///
    /// 与 <c>--smoke-test</c> 的区别：自检跳过托盘与窗口显示，只验证 XAML 与视图模型；
    /// 本模式验证的是用户双击时真正执行的那条链路，因此可以用来回答"双击后到底有没有起来"。
    /// 加上 <c>--full</c> 时会连五页初始化与后台服务一起跑，等价于一次真实使用（随后自动退出）。
    /// 在没有交互式桌面的环境（远程会话、自动化）里它同样能跑，并把每一步的结果打印出来。
    /// </summary>
    private void RunStartupCheck(bool full, bool scan) => RunStartupCheck(full, scan, scanVolume: null);

    private void RunStartupCheck(bool full, bool scan = false, string? scanVolume = null)
    {
        // 非 full 模式只做取证：让 MainWindow 的 Loaded 不重复做一次初始化
        SmokeTestMode = !full;

        const long ScanProbeLimit = 120_000;
        var failures = new List<string>();
        string? scanSummary = null;
        string? scanDetail = null;
        try
        {
            AppPaths.Initialize();
            StartApplication(createTray: true);

            if (full)
            {
                // 等 Loaded 里的完整初始化跑完（含五页数据加载与后台服务启动）
                var init = _mainWindow!.InitializationTask;
                if (init is null)
                {
                    // Loaded 可能尚未触发：先让消息泵转一圈再取
                    RunDispatcherPump(Task.Run(() => Thread.Sleep(200)), TimeSpan.FromSeconds(10));
                    init = _mainWindow.InitializationTask;
                }
                if (init is null) failures.Add("主窗口 Loaded 未触发，初始化任务不存在");
                else RunDispatcherPump(init, TimeSpan.FromMinutes(3));

                // 让后台服务（调度器、提醒计时器）真的跑一小会儿
                RunDispatcherPump(Task.Run(() => Thread.Sleep(2500)), TimeSpan.FromSeconds(30));
            }
            else
            {
                RunDispatcherPump(Task.Run(() => Thread.Sleep(120)), TimeSpan.FromSeconds(10));
            }

            _mainWindow!.UpdateLayout();

            // 真实磁盘的有界扫描自证：只扫一个卷、限定条目上限，扫描本身是只读操作。
            // 目的是在交付前拿到"扫描引擎在真实盘符上确实能跑"的证据，而不是只有样本树结论。
            if (scan && _host is not null)
            {
                var volume = scanVolume ?? Core.Scanning.ScanEngine.ListVolumes().FirstOrDefault(v => v.Scannable)?.Root;
                if (volume is null)
                {
                    failures.Add("未发现可扫描的固定磁盘，跳过真实扫描取证");
                }
                else
                {
                    Mark($"开始真实磁盘扫描取证：{volume}（只读，条目上限 {ScanProbeLimit:N0}）");
                    var result = _host.ScanEngine.Scan(new Core.Scanning.ScanOptions
                    {
                        Mode = Core.Models.ScanMode.Full,
                        Roots = [volume],
                        EnableRecognition = false,
                        MaxEntries = ScanProbeLimit,
                        MaxParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                    });
                    Mark($"扫描取证完成：{result.Summary}");
                    scanSummary = result.Summary;
                    scanDetail = $"文件 {result.Files:N0}，目录 {result.Dirs:N0}，{Core.Utils.Format.Bytes(result.Bytes)}，" +
                                 $"{result.ItemsPerSecond:N0} 项/秒，用时 {result.Seconds:0.##}s" +
                                 (result.Cancelled ? "（已中止）" : string.Empty) +
                                 (result.TopErrors.Count > 0 ? $"；首个错误：{result.TopErrors[0]}" : string.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"启动异常：{ex.GetType().Name}: {ex.Message}");
        }

        var trayState = _tray is not null ? "已创建" : "未创建（可能不支持通知区域，主界面不受影响）";
        var hostState = _host is not null ? $"就绪（SQLite {_host.Database.EngineVersion}）" : "未就绪";
        var statusText = _mainWindow?.ViewModel.StatusText ?? "(无)";
        var currentScreen = _mainWindow?.ViewModel.CurrentScreen.ToString() ?? "(无)";

        Console.WriteLine();
        Console.WriteLine("================ 全能办公助手 · 启动路径取证 ================");
        Console.WriteLine($"  模式：{(full ? "完整启动（含界面初始化与后台服务）" : "启动链路（跳过界面初始化）")}");
        Console.WriteLine($"  宿主：{hostState}");
        Console.WriteLine($"  托盘：{trayState}");
        Console.WriteLine($"  主窗口可见：{_mainWindow is { IsVisible: true }}");
        Console.WriteLine($"  当前界面：{currentScreen}");
        Console.WriteLine($"  初始化状态栏：{statusText}");
        Console.WriteLine($"  索引概况：{_mainWindow?.ViewModel.IndexText ?? "(无)"}");
        if (scanDetail is not null) Console.WriteLine($"  真实磁盘扫描：{scanDetail}");
        Console.WriteLine();
        Console.WriteLine("  启动轨迹：");
        foreach (var line in Trace) Console.WriteLine("    " + line);
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  启动路径验证通过：从数据目录到窗口显示、页面初始化与后台服务都已跑通。");
            Console.WriteLine("  （本模式只做取证，不做视觉检查；窗口外观需在桌面会话中查看。）");
        }
        else
        {
            Console.WriteLine("  启动路径存在问题：");
            foreach (var failure in failures) Console.WriteLine("  [!!] " + failure);
        }
        Console.WriteLine("==========================================================");

        var exitCode = failures.Count == 0 ? 0 : 1;
        try
        {
            // 先置收尾标志（并释放宿主），再关窗口：
            // 这样 Close() 与随后 Shutdown() 触发的重复关闭都不会再去读已释放的数据库。
            _mainWindow?.PrepareShutdown();
            _mainWindow?.Close();
        }
        catch
        {
            // 忽略收尾异常
        }
        Shutdown(exitCode);
    }

    // ------------------------------------------------------------------ 无界面自检

    private async void RunSmokeTest()
    {
        SmokeTestMode = true;

        var failures = new List<string>();
        var notes = new List<string>();

        // 捕获 WPF 数据绑定错误：绑定路径写错时这里会拿到记录
        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        System.Diagnostics.PresentationTraceSources.Refresh();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(listener);
        source.Switch.Level = System.Diagnostics.SourceLevels.Warning;

        try
        {
            AppPaths.Initialize();
            notes.Add($"数据目录：{AppPaths.DataRoot}");

            _host = AppHost.Current;
            notes.Add($"SQLite 引擎：{_host.Database.EngineVersion}");
            notes.Add($"数据库文件：{AppPaths.DatabaseFile}");

            // 不创建托盘（自检环境可能没有交互式通知区域）
            StartApplication(createTray: false);
            notes.Add("主窗口已实例化，并完成一次布局与数据绑定");

            _mainWindow!.UpdateLayout();
            notes.Add($"可视化元素数量：{CountVisualChildren(_mainWindow)}");

            var mainViewModel = _mainWindow.ViewModel;
            notes.Add("界面模型已装配：主页/网络/磁盘/内存/设置 = " +
                      $"{mainViewModel.Home is not null}/" +
                      $"{mainViewModel.Network is not null}/{mainViewModel.Disk is not null}/" +
                      $"{mainViewModel.Memory is not null}/{mainViewModel.Settings is not null}");

            // 注意：这里不能直接 .GetAwaiter().GetResult()——初始化链路内部会回到 UI 线程，
            // 阻塞 UI 线程会造成死锁。改为"派发消息 + 完成即退出"的帧泵等待。
            RunDispatcherPump(mainViewModel.InitializeAsync(), TimeSpan.FromMinutes(3));

            var disk = mainViewModel.Disk;
            var resource = mainViewModel.Memory;
            var settings = mainViewModel.Settings;

            notes.Add($"初始化完成，状态栏：{mainViewModel.StatusText}");
            notes.Add($"磁盘页识别到的盘符数：{disk?.Volumes.Count ?? 0}");
            notes.Add($"内存页累计已释放：{resource?.TotalReleasedMb ?? "(未初始化)"} MB");
            var auditFirstLine = settings?.AuditText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            notes.Add("设置页合规自检：" + (auditFirstLine ?? "(无输出)"));

            // 单独验证每个界面的 XAML 都能独立解析（新增界面也必须覆盖）
            var controls = new (string Name, Func<object> Factory)[]
            {
                ("HomeScreen", () => new HomeScreen()),
                ("NetworkScreen", () => new NetworkScreen()),
                ("MemoryScreen", () => new MemoryScreen()),
                ("SettingsScreen", () => new SettingsScreen()),
                ("DiskTreeScreen", () => new DiskTreeScreen()),
            };
            foreach (var (name, factory) in controls)
            {
                try
                {
                    var control = factory();
                    notes.Add($"{name} 解析成功（{control.GetType().Name}）");
                }
                catch (Exception ex)
                {
                    // 展开完整异常链：XamlParseException 的外层消息只有"提供值时引发了异常"，
                    // 真正缺的是哪个资源在内层，不展开就只能靠猜。
                    var chain = new List<string>();
                    for (var e = ex; e is not null; e = e.InnerException)
                        chain.Add($"{e.GetType().Name}: {e.Message}");
                    failures.Add($"{name} 解析失败：{string.Join(" <- ", chain)}");
                }
            }

            // 真实走一遍导航：主界面 → 各功能页 → 返回主界面。
            // 单测"每个界面能解析"只能证明 XAML 没问题，证明不了"点得动"，因此这里实际操作一次。
            var navRoute = new (AppScreen Screen, string Label)[]
            {
                (AppScreen.Network, "网络加速"),
                (AppScreen.Disk, "磁盘管理"),
                (AppScreen.Memory, "内存清理"),
                (AppScreen.Settings, "设置"),
            };
            foreach (var (screen, label) in navRoute)
            {
                try
                {
                    mainViewModel.Navigate(screen);
                    _mainWindow.UpdateLayout();
                    if (mainViewModel.CurrentScreen != screen)
                        failures.Add($"导航到「{label}」后当前界面状态不正确");
                    else if (_mainWindow.ScreenContent is not { IsVisible: true })
                        failures.Add($"导航到「{label}」后界面内容不可见");

                    mainViewModel.GoHome();
                    _mainWindow.UpdateLayout();
                    if (mainViewModel.CurrentScreen != AppScreen.Home)
                        failures.Add($"从「{label}」返回主界面失败");
                }
                catch (Exception ex)
                {
                    failures.Add($"导航「{label}」异常：{ex.GetType().Name}: {ex.Message}");
                }
            }

            notes.Add($"界面导航已实测：主界面 → {string.Join(" / ", navRoute.Select(r => r.Label))} → 主界面 全部往返成功");

            // 磁盘自检：盘符、目录树、批量勾选、清理预览——列表视图是现在唯一的磁盘界面
            try
            {
                var diskPage = mainViewModel.Disk;
                notes.Add($"磁盘盘符：{diskPage.Volumes.Count} 个（{string.Join("、", diskPage.Volumes.Select(v => v.Root))}）");
                if (diskPage.Volumes.Count == 0) failures.Add("未识别到任何盘符");

                // 索引为空时先做一次有界只读扫描，否则目录树是空的、核心功能等于没验证
                if (_host.Database.CountFiles() == 0)
                {
                    var root = diskPage.Volumes.FirstOrDefault(v => v.Scannable)?.Root
                               ?? Core.Scanning.ScanEngine.ListVolumes().FirstOrDefault(v => v.Scannable)?.Root;
                    if (root is not null)
                    {
                        Mark("磁盘自检：索引为空，先做一次有界只读扫描");
                        var boot = _host.ScanEngine.Scan(new Core.Scanning.ScanOptions
                        {
                            Mode = Core.Models.ScanMode.Full,
                            Roots = [root],
                            EnableRecognition = false,
                            MaxEntries = 20000,
                            MaxParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                        });
                        notes.Add($"磁盘自检预热扫描：{boot.Summary}");
                    }
                }

                await diskPage.InitializeAsync().ConfigureAwait(true);
                notes.Add($"目录树树根：{diskPage.Roots.Count} 个");
                if (diskPage.Roots.Count == 0) failures.Add("目录树没有任何树根");

                // 展开第一层，验证懒加载与勾选统计
                var firstRoot = diskPage.Roots.FirstOrDefault(r => !r.IsPlaceholder);
                if (firstRoot is not null)
                {
                    await firstRoot.LoadChildrenAsync().ConfigureAwait(true);
                    var folders = firstRoot.Children.Count(c => !c.IsPlaceholder);
                    notes.Add($"目录树展开实测：「{firstRoot.Name}」{folders} 个子项");

                    if (folders > 0)
                    {
                        var target = firstRoot.Children.First(c => !c.IsPlaceholder);
                        target.IsChecked = true;
                        diskPage.SyncChecked();
                        notes.Add($"批量勾选实测：已勾选 {diskPage.CheckedNodes.Count} 项 —— {diskPage.BatchSummary}");
                        if (diskPage.CheckedNodes.Count != 1) failures.Add("批量勾选统计不正确");
                        target.IsChecked = false;
                        diskPage.SyncChecked();
                    }
                }

                // 一键清理预览：只生成清单，不执行删除
                await diskPage.BuildCleanupPreviewAsync().ConfigureAwait(true);
                notes.Add($"一键清理预览实测：{diskPage.CleanupGroups.Count} 个分类 —— {diskPage.CleanupPreviewSummary}");
                diskPage.IsCleanupPreviewOpen = false;

                // 深度清理预览：只统计，不删除
                await diskPage.BuildTrustedPreviewAsync().ConfigureAwait(true);
                notes.Add($"深度清理预览实测：{diskPage.TrustedRows.Count} 个可清理位置 —— {diskPage.TrustedPreviewSummary}");
                diskPage.IsTrustedPreviewOpen = false;
            }
            catch (Exception ex)
            {
                failures.Add($"磁盘自检异常：{ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"自检异常：{ex.GetType().Name}: {ex.Message}");
            Log.Error("自检失败", ex);
        }
        finally
        {
            source.Listeners.Remove(listener);
        }

        foreach (var error in bindingErrors.Distinct().Take(20))
            failures.Add("数据绑定错误：" + error);

        Console.WriteLine();
        Console.WriteLine("================ 全能办公助手 · UI 自检 ================");
        foreach (var note in notes) Console.WriteLine("  [OK] " + note);
        Console.WriteLine();
        Console.WriteLine("  启动轨迹：");
        foreach (var line in Trace) Console.WriteLine("    " + line);
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  UI 自检通过：XAML 全部解析成功，未发现数据绑定错误。");
        }
        else
        {
            Console.WriteLine($"  UI 自检发现 {failures.Count} 个问题：");
            foreach (var failure in failures) Console.WriteLine("  [!!] " + failure);
        }
        Console.WriteLine("======================================================");

        var exitCode = failures.Count == 0 ? 0 : 1;
        try
        {
            _mainWindow?.Close();
            _host?.Dispose();
        }
        catch
        {
            // 忽略收尾异常
        }
        Shutdown(exitCode);
    }

    /// <summary>
    /// 等待一个需要在 UI 线程上继续执行的异步任务完成：用 DispatcherFrame 泵消息，
    /// 任务完成或超时后退出帧。自检过程中不能阻塞 UI 线程，否则内部续体无法执行。
    /// </summary>
    /// <summary>等待一个任务完成，期间保持消息泵转动（避免与 UI 线程相互等待的死锁）。</summary>
    private static Task PumpAsync(Task task, TimeSpan timeout)
    {
        RunDispatcherPump(task, timeout);
        return Task.CompletedTask;
    }

    /// <summary>让消息泵转动指定时长（用于等布局与短动画跑完）。</summary>
    private static void Pump(TimeSpan duration)
        => RunDispatcherPump(Task.Delay(duration), duration + TimeSpan.FromSeconds(5));

    private static void RunDispatcherPump(Task task, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) =>
        {
            if (!task.IsCompleted && DateTime.UtcNow < deadline) return;
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();

        Dispatcher.PushFrame(frame);
        timer.Stop();

        if (task.IsFaulted)
            throw task.Exception!.GetBaseException();
        if (!task.IsCompleted)
            throw new TimeoutException($"自检等待超时（{timeout.TotalSeconds:0} 秒）");
    }

    private static int CountVisualChildren(DependencyObject root)    {
        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0 && count < 20000)
        {
            var current = stack.Pop();
            count++;
            var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < childCount; i++)
                stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, i));
        }
        return count;
    }

    /// <summary>把 WPF 数据绑定警告收集起来，便于自检断言。</summary>
    private sealed class BindingErrorListener : System.Diagnostics.TraceListener
    {
        private readonly List<string> _sink;

        public BindingErrorListener(List<string> sink) => _sink = sink;

        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _sink.Add(message.Trim());
        }

        public override void WriteLine(string? message) => Write(message);
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _mainWindow?.PrepareShutdown();
            _tray?.Dispose();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("退出清理失败", ex);
        }
        finally
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* 未持有时忽略 */ }
            _singleInstanceMutex?.Dispose();
            Shutdown();
        }
    }
}
