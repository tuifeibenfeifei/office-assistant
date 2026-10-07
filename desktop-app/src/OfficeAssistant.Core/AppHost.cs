using OfficeAssistant.Core.Cleanup;
using OfficeAssistant.Core.Compliance;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Homework;
using OfficeAssistant.Core.Network;
using OfficeAssistant.Core.Recognition;
using OfficeAssistant.Core.Resources;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Scanning;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core;

/// <summary>
/// 应用宿主：集中装配四大功能模块并管理生命周期。
/// 分层对应《功能说明》三·分层架构：UI 层 → 桥接层 → 核心服务层 → 系统接口层。
/// 本类属于核心服务层的装配点，UI 只通过它访问能力，不直接触碰系统接口。
/// </summary>
public sealed class AppHost : IDisposable
{
    private static readonly Lazy<AppHost> InstanceHolder = new(() => new AppHost());

    public static AppHost Current => InstanceHolder.Value;

    private bool _disposed;

    private AppHost()
    {
        AppPaths.Initialize();
        Log.MinimumLevel = LogLevel.Info;
        Log.Info($"========== {AppPaths.ProductName} 启动 ==========");
        Log.Info($"数据目录：{AppPaths.DataRoot}");

        Database = new Database(AppPaths.DatabaseFile);
        Log.Info($"SQLite 已就绪（系统组件版本 {Database.EngineVersion}）：{AppPaths.DatabaseFile}");

        Protection = new ProtectionEngine(Database);
        OperationExecutor = new OperationExecutor(Database, Protection);
        Recognition = new RecognitionEngine();
        ScanEngine = new ScanEngine(Database, Protection, Recognition);
        CleanupEngine = new CleanupEngine(Database, Protection, OperationExecutor);
        ResourceOptimizer = new ResourceOptimizer(Database);
        ResourceScheduler = new ResourceScheduler(Database, ResourceOptimizer);
        GitConfig = new GitConfigEngine(Database);
        Homework = new HomeworkService(Database);
        Settings = new AppSettings(Database);

        CleanupRules.Seed(Database);
        SeedFileTypes();
    }

    public Database Database { get; }

    public AppSettings Settings { get; }

    public ProtectionEngine Protection { get; }

    public OperationExecutor OperationExecutor { get; }

    public RecognitionEngine Recognition { get; }

    public ScanEngine ScanEngine { get; }

    public CleanupEngine CleanupEngine { get; }

    public ResourceOptimizer ResourceOptimizer { get; }

    public ResourceScheduler ResourceScheduler { get; }

    public GitConfigEngine GitConfig { get; }

    public HomeworkService Homework { get; }

    /// <summary>当前进程是否以管理员身份运行（决定待机列表清理、部分删除操作是否可用）。</summary>
    public bool IsElevated => NativeMemory.IsElevated();

    /// <summary>启动后台服务（调度器、自动同步）。</summary>
    public void StartBackgroundServices()
    {
        try
        {
            if (Settings.ResourceAutoStart) ResourceScheduler.Start();
        }
        catch (Exception ex)
        {
            Log.Error("启动资源管家调度器失败", ex);
        }

        try
        {
            if (Settings.HomeworkAutoSyncEnabled) Homework.StartAutoSync();
        }
        catch (Exception ex)
        {
            Log.Error("启动学习通自动同步失败", ex);
        }
    }

    /// <summary>运行合规红线回归（扫描本程序目录的二进制）。</summary>
    public ComplianceReport RunComplianceAudit() => ComplianceAudit.RunBinaryAudit(AppContext.BaseDirectory);

    private void SeedFileTypes()
    {
        try
        {
            var count = Database.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM file_types;");
            if (count > 0) return;
            using var tx = Database.Connection.BeginTransaction();
            foreach (var rule in FileTypeTable.Rules)
            {
                Database.Connection.Execute(
                    "INSERT OR REPLACE INTO file_types (ext, mime, magic, category) VALUES (?, ?, ?, ?);",
                    rule.Ext, rule.Mime, rule.MagicName, (int)rule.Category);
            }
            Log.Info($"类型规则表已初始化：{FileTypeTable.Rules.Count} 条");
        }
        catch (Exception ex)
        {
            Log.Warn($"初始化类型规则表失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ResourceScheduler.Dispose(); } catch { /* 忽略 */ }
        try { Homework.Dispose(); } catch { /* 忽略 */ }
        try { ScanEngine.Cancel(); } catch { /* 忽略 */ }
        try { Database.Dispose(); } catch { /* 忽略 */ }
        Log.Info("========== 应用已退出 ==========");
    }
}

/// <summary>用户设置（settings 表），集中读写并给出默认值。</summary>
public sealed class AppSettings
{
    private readonly Database _db;

    public AppSettings(Database db) => _db = db;

    private bool GetBool(string key, bool fallback)
    {
        var raw = _db.GetSetting(key);
        return raw is null ? fallback : raw != "0";
    }

    private void SetBool(string key, bool value) => _db.SetSetting(key, value ? "1" : "0");

    private int GetInt(string key, int fallback)
        => int.TryParse(_db.GetSetting(key), out var value) ? value : fallback;

    /// <summary>随主程序启动即启动资源管家调度器（即"自动清理内存"开关）。</summary>
    public bool ResourceAutoStart
    {
        get => GetBool("app.resource.autostart", false);
        set => SetBool("app.resource.autostart", value);
    }

    public bool HomeworkAutoSyncEnabled
    {
        get => GetBool("app.homework.autosync", false);
        set => SetBool("app.homework.autosync", value);
    }

    /// <summary>关闭窗口时最小化到托盘（默认开启）。</summary>
    public bool MinimizeToTray
    {
        get => GetBool("app.tray.minimize", true);
        set => SetBool("app.tray.minimize", value);
    }

    public bool ShowTrayBadge
    {
        get => GetBool("app.tray.badge", true);
        set => SetBool("app.tray.badge", value);
    }

    /// <summary>
    /// 开机启动（登录时自动运行）。
    ///
    /// 以注册表 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 为唯一事实来源，
    /// 不再另存一份到数据库：两处记录一旦不同步，用户就会看到开关与实际行为不一致。
    /// 写当前用户配置单元不需要管理员权限，也便于卸载时清理。
    /// </summary>
    public bool AutoStart
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartKeyPath);
                var value = key?.GetValue(AutoStartValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取开机启动设置失败：{ex.Message}");
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoStartKeyPath, writable: true);
                if (key is null) throw new InvalidOperationException("无法打开当前用户的启动项注册表键。");

                if (value)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exe))
                        throw new InvalidOperationException("无法确定程序自身路径，开机启动未设置。");
                    key.SetValue(AutoStartValueName, $"\"{exe}\"", Microsoft.Win32.RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
                }

                Log.Info($"开机启动已{(value ? "开启" : "关闭")}。");
            }
            catch (Exception ex)
            {
                // 写注册表可能被安全软件或组策略拦截；明确抛出让界面提示用户，不静默失败
                Log.Warn($"写入开机启动设置失败：{ex.Message}");
                throw;
            }
        }
    }

    private const string AutoStartKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "OfficeAssistant";

    /// <summary>扫描范围：为空表示全部固定磁盘。</summary>
    public List<string> ScanRoots
    {
        get
        {
            var raw = _db.GetSetting("scan.roots");
            return string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        set => _db.SetSetting("scan.roots", value.Count == 0 ? string.Empty : string.Join('\n', value));
    }

    public bool OnlySystemDrive
    {
        get => GetBool("scan.onlysystemdrive", false);
        set => SetBool("scan.onlysystemdrive", value);
    }

    public int ScanParallelism
    {
        get => Math.Clamp(GetInt("scan.parallelism", Math.Clamp(Environment.ProcessorCount, 2, 8)), 1, 32);
        set => _db.SetSetting("scan.parallelism", Math.Clamp(value, 1, 32).ToString());
    }

    public bool ScanWithRecognition
    {
        get => GetBool("scan.recognition", true);
        set => SetBool("scan.recognition", value);
    }

    public bool IncrementalScan
    {
        get => GetBool("scan.incremental", true);
        set => SetBool("scan.incremental", value);
    }

    /// <summary>是否已完成首次合规说明确认（功能一 F1-06）。</summary>
    public bool ComplianceNoticeAccepted
    {
        get => GetBool("compliance.notice.accepted", false);
        set => SetBool("compliance.notice.accepted", value);
    }

    /// <summary>网络优化功能总开关（功能可整体停用）。</summary>
    public bool NetworkFeatureEnabled
    {
        get => GetBool("network.enabled", true);
        set => SetBool("network.enabled", value);
    }

    /// <summary>执行高危操作（如批量删除）时需要输入确认词。</summary>
    public const string DangerConfirmWord = "确认";
}
