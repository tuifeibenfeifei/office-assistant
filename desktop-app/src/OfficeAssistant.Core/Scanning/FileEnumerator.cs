using System.Runtime.InteropServices;

namespace OfficeAssistant.Core.Scanning;

/// <summary>单次枚举目录得到的条目。</summary>
public readonly record struct RawEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsReparsePoint,
    long Size,
    long MtimeUnix,
    uint Attributes);

/// <summary>
/// 高速目录枚举。
///
/// 使用 FindFirstFileExW + FindExInfoBasic：不请求 8.3 短名（该项在含大量文件的目录上开销显著），
/// 只取 <c>WIN32_FIND_DATAW</c> 中真正需要的字段。相比 Directory.EnumerateFileSystemEntries
/// 每次返回字符串并另发起一次属性查询，这里一次调用即拿到大小、时间与属性，首扫吞吐提升明显。
/// </summary>
public static class FileEnumerator
{
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPathNotFound = 3;

    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private static readonly IntPtr InvalidHandle = new(-1);

    public static int LastErrorCode { get; private set; }

    /// <summary>枚举目录。返回 null 表示目录不可访问（权限不足等），调用方据此计数并跳过。</summary>
    public static List<RawEntry>? Enumerate(string directory, CancellationToken token = default)
    {
        var results = new List<RawEntry>(64);
        var searchPattern = directory.EndsWith('\\') ? directory + "*" : directory + "\\*";
        var handle = FindFirstFileExW(searchPattern, FindExInfoBasic, out var data,
            FindExSearchNameMatch, IntPtr.Zero, 0);

        if (handle == InvalidHandle)
        {
            LastErrorCode = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var name = data.cFileName;
                if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                {
                    var isDir = (data.dwFileAttributes & FileAttributeDirectory) != 0;
                    var isReparse = (data.dwFileAttributes & FileAttributeReparsePoint) != 0;
                    var size = isDir ? 0 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    var mtime = ToUnixSeconds(data.ftLastWriteTime);
                    var full = searchPattern[..^1] + name;
                    results.Add(new RawEntry(name, full, isDir, isReparse, size, mtime, data.dwFileAttributes));
                }

                if (!FindNextFileW(handle, out data)) break;
            }
        }
        finally
        {
            FindClose(handle);
        }

        return results;
    }

    /// <summary>取目录自身的最后写入时间（作为目录指纹的一半）。</summary>
    public static long GetDirectoryMtime(string directory)
    {
        var handle = FindFirstFileExW(directory, FindExInfoBasic, out var data,
            FindExSearchNameMatch, IntPtr.Zero, 0);
        if (handle == InvalidHandle)
        {
            LastErrorCode = Marshal.GetLastWin32Error();
            return 0;
        }
        try
        {
            return ToUnixSeconds(data.ftLastWriteTime);
        }
        finally
        {
            FindClose(handle);
        }
    }

    public static string DescribeLastError() => LastErrorCode switch
    {
        ErrorAccessDenied => "访问被拒绝",
        ErrorFileNotFound => "目录不存在",
        ErrorPathNotFound => "路径无效",
        _ => $"Win32 错误 {LastErrorCode}",
    };

    private static long ToUnixSeconds(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        var high = (long)ft.dwHighDateTime << 32;
        var low = (uint)ft.dwLowDateTime;
        var fileTime = high | low;
        if (fileTime == 0) return 0;
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)).ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
