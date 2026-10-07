using System.Diagnostics;
using System.Runtime.InteropServices;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Resources;

/// <summary>进程快照条目。</summary>
public sealed record ProcessInfo(
    int Pid,
    string Name,
    long WorkingSetBytes,
    long PrivateBytes,
    TimeSpan CpuTime,
    TimeSpan IdleTime,
    DateTime StartTime,
    bool IsForeground,
    bool IsCritical,
    int SessionId);

/// <summary>单个进程的清理结果。</summary>
public sealed record ProcessCleanupResult(
    int Pid,
    string Name,
    long ReleasedBytes,
    bool Success,
    string? Error);

/// <summary>用户可见的清理报告（对应「释放量、耗时、受影响进程清单」）。</summary>
public sealed class CleanupReport
{
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime FinishedAt { get; set; } = DateTime.Now;
    public string Trigger { get; init; } = "手动";
    public ulong MemoryBefore { get; set; }
    public ulong MemoryAfter { get; set; }
    public ulong StandbyBefore { get; set; }
    public ulong StandbyAfter { get; set; }
    public int ScannedProcesses { get; set; }
    public int CompressedProcesses { get; set; }
    public int SkippedProcesses { get; set; }
    public int FailedProcesses { get; set; }
    public int PriorityLowered { get; set; }
    public int PriorityRestored { get; set; }
    public bool StandbyPurged { get; set; }
    public bool StandbyRequested { get; set; }
    public bool WasElevated { get; set; }
    public List<ProcessCleanupResult> Processes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public double Seconds => (FinishedAt - StartedAt).TotalSeconds;

    public long WorkingSetReleasedBytes =>
        MemoryBefore > MemoryAfter ? (long)(MemoryBefore - MemoryAfter) : 0;

    public long StandbyReleasedBytes =>
        StandbyBefore > StandbyAfter ? (long)(StandbyBefore - StandbyAfter) : 0;

    public string Summary =>
        $"释放物理内存 {Format.Bytes((long)WorkingSetReleasedBytes)}（工作集压缩）" +
        (StandbyPurged ? $" + 待机缓存 {Format.Bytes((long)StandbyReleasedBytes)}" : string.Empty) +
        $"，耗时 {Format.Duration(Seconds)}；压缩 {CompressedProcesses} 个进程，跳过 {SkippedProcesses} 个（前台/白名单/关键进程），失败 {FailedProcesses} 个。";

    /// <summary>技术边界说明：如实告知工作集压缩是短期整理。</summary>
    public const string BoundaryNotice =
        "说明：工作集压缩属于短期整理——被换出的空闲页面在进程再次访问时会由系统按需重新调入，" +
        "因此释放量是即时值，不承诺持续低占用。在不结束进程的前提下，内存回收存在客观上限。";
}

/// <summary>
/// 智能资源管家（功能四）：工作集压缩、Standby 列表清理、进程优先级优化与清理报告。
///
/// 硬性约束：
/// - 不结束任何进程（全程不调用 TerminateProcess，仅调整工作集与优先级）；
/// - 前台进程与用户白名单进程永不触碰；
/// - 优先级的调整在清理结束时恢复原值。
/// </summary>
public sealed class ResourceOptimizer
{
    private readonly Database _db;
    private readonly HashSet<string> _whitelist;
    private readonly HashSet<string> _criticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "lsaiso", "svchost", "dwm", "fontdrvhost", "ctfmon", "sihost", "taskhostw", "audiodg",
        "memory compression", "secure system", "msmpeng", "nissrv", "securityhealthservice",
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchapp",
        "wudfhost", "spoolsv", "conhost", "runtimebroker", "dllhost", "wmiprvse", "logonui",
        "textinputhost", "applicationframehost", "systemsettings",
    };

    /// <summary>默认不触碰的进程白名单（用户可在设置中扩展）。</summary>
    public static readonly string[] DefaultWhitelist =
    [
        "全能办公助手", "OfficeAssistant", "devenv", "MSBuild", "dotnet", "java", "python", "explorer",
        "node", "Code", "chrome", "msedge", "firefox", "WeChat", "QQ", "TIM", "DingTalk",
        "Teams", "winword", "excel", "powerpnt", "wps", "et", "wpp", "notepad", "Obsidian",
        "Typora", "PotPlayer", "vlc", "OBS64", "obs64", "Zoom", "TencentMeeting",
    ];

    public ResourceOptimizer(Database? db = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _whitelist = new HashSet<string>(DefaultWhitelist, StringComparer.OrdinalIgnoreCase);
        LoadUserWhitelist();
    }

    public bool IsElevated => NativeMemory.IsElevated();

    public MemorySnapshot ReadMemory() => NativeMemory.ReadMemory();

    public IReadOnlyCollection<string> Whitelist
    {
        get
        {
            lock (_whitelist) return _whitelist.ToList();
        }
    }

    private void LoadUserWhitelist()
    {
        var raw = _db.GetSetting("process.whitelist");
        if (string.IsNullOrWhiteSpace(raw)) return;
        lock (_whitelist)
        {
            foreach (var name in raw.Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cleaned = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
                if (cleaned.Length > 0) _whitelist.Add(cleaned);
            }
        }
    }

    public void SaveWhitelist(IEnumerable<string> names)
    {
        lock (_whitelist)
        {
            _whitelist.Clear();
            foreach (var n in DefaultWhitelist) _whitelist.Add(n);
            foreach (var name in names)
            {
                var cleaned = name.Trim();
                if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^4];
                if (cleaned.Length > 0) _whitelist.Add(cleaned);
            }
        }
        _db.SetSetting("process.whitelist", string.Join('\n', Whitelist.OrderBy(x => x)));
    }

    public bool IsWhitelisted(string processName)
    {
        var cleaned = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        lock (_whitelist) return _whitelist.Contains(cleaned);
    }

    /// <summary>枚举进程快照（工作集、CPU 时间、空闲时长、前台判定）。</summary>
    public List<ProcessInfo> Snapshot()
    {
        var foreground = GetForegroundProcessId();
        var result = new List<ProcessInfo>(256);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                TimeSpan idle;
                try { idle = DateTime.Now - process.StartTime; }
                catch { idle = TimeSpan.Zero; }

                DateTime start;
                try { start = process.StartTime; }
                catch { start = DateTime.MinValue; }

                result.Add(new ProcessInfo(
                    process.Id,
                    name,
                    SafeWorkingSet(process),
                    SafePrivateBytes(process),
                    SafeCpuTime(process),
                    idle,
                    start,
                    process.Id == foreground,
                    IsCritical(process.Id, name),
                    SafeSessionId(process)));
            }
            catch
            {
                // 进程可能在枚举过程中退出
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static long SafePrivateBytes(Process p)
    {
        try { return p.PrivateMemorySize64; } catch { return 0; }
    }

    private static TimeSpan SafeCpuTime(Process p)
    {
        try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
    }

    private static int SafeSessionId(Process p)
    {
        try { return p.SessionId; } catch { return -1; }
    }

    public bool IsCritical(int pid, string name)
        => pid <= 4 || _criticalNames.Contains(name);

    /// <summary>用户空闲时长（无键盘鼠标输入）。</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int GetForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>执行一次内存整理。前台进程与白名单进程豁免，不结束任何进程。</summary>
    public CleanupReport RunCleanup(ResourceCleanupOptions options, Action<string>? progress = null)
    {
        var report = new CleanupReport
        {
            Trigger = options.Trigger,
            MemoryBefore = ReadMemory().PhysicalUsedBytes,
            StandbyBefore = ReadMemory().StandbyBytes,
            WasElevated = IsElevated,
        };

        try
        {
            if (report.WasElevated)
            {
                // 提权后尝试获取跨账户操作所需特权；失败不影响主流程
                NativeMemory.TryEnablePrivilege("SeProfileSingleProcessPrivilege");
                NativeMemory.TryEnablePrivilege("SeIncreaseQuotaPrivilege");
            }

            var foregroundPid = GetForegroundProcessId();
            var selfPid = Environment.ProcessId;
            var snapshot = Snapshot();
            report.ScannedProcesses = snapshot.Count;
            progress?.Invoke($"已枚举 {snapshot.Count} 个进程，开始工作集压缩…");

            var prioritySnapshots = new List<(int Pid, string Name, uint Previous)>();

            foreach (var info in snapshot)
            {
                if (info.Pid == selfPid || info.Pid == foregroundPid || info.Pid <= 4 || info.IsCritical)
                {
                    report.SkippedProcesses++;
                    continue;
                }

                if (IsWhitelisted(info.Name))
                {
                    report.SkippedProcesses++;
                    continue;
                }

                if (info.WorkingSetBytes < options.MinWorkingSetBytes)
                {
                    report.SkippedProcesses++;
                    continue;
                }

                if (options.SkipSessionMismatch && info.SessionId >= 0)
                {
                    var ownSession = Process.GetCurrentProcess().SessionId;
                    if (info.SessionId != ownSession)
                    {
                        report.SkippedProcesses++;
                        continue;
                    }
                }

                var success = NativeMemory.TryEmptyWorkingSet(info.Pid, out var error);
                if (success)
                {
                    report.CompressedProcesses++;
                    report.Processes.Add(new ProcessCleanupResult(info.Pid, info.Name, info.WorkingSetBytes, true, null));
                }
                else
                {
                    report.FailedProcesses++;
                    report.Processes.Add(new ProcessCleanupResult(info.Pid, info.Name, 0, false, error));
                }

                // 优先级优化：长时间低交互的进程降为"低于正常"，结束时恢复
                if (options.AdjustPriority && info.IdleTime >= options.PriorityIdleThreshold)
                {
                    if (NativeMemory.TrySetPriorityClass(info.Pid, NativeMemory.BELOW_NORMAL_PRIORITY_CLASS,
                            out var previous, out _))
                    {
                        if (previous != NativeMemory.BELOW_NORMAL_PRIORITY_CLASS)
                        {
                            prioritySnapshots.Add((info.Pid, info.Name, previous));
                            report.PriorityLowered++;
                        }
                    }
                }
            }

            // Standby 列表清理（默认关闭）
            if (options.PurgeStandbyList)
            {
                report.StandbyRequested = true;
                if (!report.WasElevated)
                {
                    report.Warnings.Add("待机列表清理需要管理员权限，本次已跳过。请以管理员身份运行后重试。");
                }
                else
                {
                    report.StandbyPurged = NativeMemory.PurgeStandbyList(options.LowPriorityStandbyOnly);
                    if (!report.StandbyPurged) report.Warnings.Add("待机列表清理调用未成功，可能被系统策略限制。");
                }
            }

            // 恢复优先级
            foreach (var (pid, _, previous) in prioritySnapshots)
            {
                if (NativeMemory.TrySetPriorityClass(pid, previous == 0 ? NativeMemory.NORMAL_PRIORITY_CLASS : previous,
                        out _, out _))
                    report.PriorityRestored++;
            }

            Thread.Sleep(options.SettleMilliseconds);
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"清理过程出现异常：{ex.Message}");
            Log.Error("内存清理异常", ex);
        }
        finally
        {
            var after = ReadMemory();
            report.MemoryAfter = after.PhysicalUsedBytes;
            report.StandbyAfter = after.StandbyBytes;
            report.FinishedAt = DateTime.Now;
        }

        Log.Info($"资源管家清理：{report.Summary}");
        return report;
    }

    /// <summary>只读的高占用进程提示（不结束进程）。</summary>
    public List<ProcessInfo> TopConsumers(int count = 10)
        => Snapshot()
            .Where(p => p.Pid > 4)
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(count)
            .ToList();
}

/// <summary>一次清理的参数。</summary>
public sealed class ResourceCleanupOptions
{
    public string Trigger { get; set; } = "手动";

    /// <summary>工作集小于该值的进程跳过，避免无意义的系统调用。</summary>
    public long MinWorkingSetBytes { get; set; } = 20L * 1024 * 1024;

    public bool PurgeStandbyList { get; set; }

    public bool LowPriorityStandbyOnly { get; set; } = true;

    public bool AdjustPriority { get; set; } = true;

    /// <summary>空闲超过该时长才降优先级（默认 30 分钟）。</summary>
    public TimeSpan PriorityIdleThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>仅处理当前会话的进程，避免影响其他登录用户。</summary>
    public bool SkipSessionMismatch { get; set; } = true;

    /// <summary>清理后等待系统统计稳定再取"清理后"内存值。</summary>
    public int SettleMilliseconds { get; set; } = 900;
}
