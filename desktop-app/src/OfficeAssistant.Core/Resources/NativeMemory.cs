using System.Runtime.InteropServices;

namespace OfficeAssistant.Core.Resources;

/// <summary>系统内存快照。</summary>
public readonly record struct MemorySnapshot(
    ulong TotalBytes,
    ulong AvailableBytes,
    ulong PhysicalUsedBytes,
    ulong PageFileTotalBytes,
    ulong PageFileAvailableBytes,
    ulong CommittedBytes,
    ulong StandbyBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : (double)PhysicalUsedBytes / TotalBytes * 100.0;
}

/// <summary>
/// 智能资源管家所需的 Windows API 封装：物理内存与 Standby 列表统计、
/// 工作集压缩（EmptyWorkingSet）、进程优先级调整、令牌特权提升检测。
/// </summary>
public static class NativeMemory
{
    private const int SystemMemoryListInformation = 0x0050;
    private const int SystemMemoryUsageInformation = 0x004C;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    public const uint IDLE_PRIORITY_CLASS = 0x00000040;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>SYSTEM_MEMORY_USAGE_INFORMATION：提供 Standby 缓存的一手统计数据。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_MEMORY_USAGE_INFORMATION
    {
        public ulong TotalPhysicalBytes;
        public ulong AvailableBytes;
        public long ResidentAvailableBytes;
        public ulong CommittedBytes;
        public ulong CommitLimitBytes;
        public ulong CommittedPeakBytes;
        public ulong VirtualAvailableBytes;
        public ulong StandbyCacheNormalPriorityBytes;
        public ulong StandbyCacheReserveBytes;
        public ulong StandbyCacheCoreBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        ref TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;
    private const int TokenElevationClass = 20;

    /// <summary>读取系统内存快照（含 Standby 缓存统计）。</summary>
    public static MemorySnapshot ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return default;

        ulong standby = 0;
        ulong committed = 0;

        var usageSize = Marshal.SizeOf<SYSTEM_MEMORY_USAGE_INFORMATION>();
        var usageBuffer = Marshal.AllocHGlobal(usageSize);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryUsageInformation, usageBuffer, usageSize, out _) == 0)
            {
                var usage = Marshal.PtrToStructure<SYSTEM_MEMORY_USAGE_INFORMATION>(usageBuffer);
                standby = usage.StandbyCacheNormalPriorityBytes
                          + usage.StandbyCacheReserveBytes
                          + usage.StandbyCacheCoreBytes;
                committed = usage.CommittedBytes;
            }
        }
        catch
        {
            // 该信息类在部分系统版本上不可用，忽略
        }
        finally
        {
            Marshal.FreeHGlobal(usageBuffer);
        }

        var used = status.ullTotalPhys > status.ullAvailPhys ? status.ullTotalPhys - status.ullAvailPhys : 0;

        return new MemorySnapshot(
            status.ullTotalPhys,
            status.ullAvailPhys,
            used,
            status.ullTotalPageFile,
            status.ullAvailPageFile,
            committed,
            standby);
    }

    /// <summary>
    /// 清空 Standby 列表（需管理员权限）。系统在权限不足时直接返回错误码，不产生副作用。
    /// </summary>
    public static bool PurgeStandbyList(bool lowPriorityOnly = false)
    {
        try
        {
            var command = lowPriorityOnly ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, command);
                return NtQuerySystemInformation(SystemMemoryListInformation, buffer, sizeof(int), out _) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            var elevation = new TOKEN_ELEVATION();
            var size = Marshal.SizeOf<TOKEN_ELEVATION>();
            if (!GetTokenInformation(token, TokenElevationClass, ref elevation, size, out _)) return false;
            return elevation.TokenIsElevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>尝试启用指定特权（提权后可用于跨账户进程）。</summary>
    public static bool TryEnablePrivilege(string privilegeName)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;
            if (!LookupPrivilegeValueW(null, privilegeName, out var luid)) return false;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>打开进程并执行工作集压缩（EmptyWorkingSet）。</summary>
    public static bool TryEmptyWorkingSet(int processId, out string? error)
    {
        error = null;
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = $"无法打开进程（Win32 错误 {Marshal.GetLastWin32Error()}）";
            return false;
        }
        try
        {
            if (!EmptyWorkingSet(handle))
            {
                error = $"EmptyWorkingSet 失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool TrySetPriorityClass(int processId, uint priorityClass, out uint previous, out string? error)
    {
        previous = 0;
        error = null;
        var handle = OpenProcess(PROCESS_SET_QUOTA, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = $"无法打开进程（Win32 错误 {Marshal.GetLastWin32Error()}）";
            return false;
        }
        try
        {
            previous = GetPriorityClass(handle);
            if (!SetPriorityClass(handle, priorityClass))
            {
                error = $"SetPriorityClass 失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string DescribePriorityClass(uint priorityClass) => priorityClass switch
    {
        IDLE_PRIORITY_CLASS => "低",
        BELOW_NORMAL_PRIORITY_CLASS => "低于正常",
        NORMAL_PRIORITY_CLASS => "正常",
        ABOVE_NORMAL_PRIORITY_CLASS => "高于正常",
        HIGH_PRIORITY_CLASS => "高",
        REALTIME_PRIORITY_CLASS => "实时",
        _ => $"未知(0x{priorityClass:X})",
    };
}
