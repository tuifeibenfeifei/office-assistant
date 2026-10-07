using System.Runtime.InteropServices;

namespace OfficeAssistant.Core.Safety;

/// <summary>
/// 回收站/移动/重命名等文件操作，统一经 Windows Shell 的 <c>IFileOperation</c> 完成。
///
/// 关键点（对应《功能说明》三·技术实现要点）：
/// - 删除一律调用回收站 API，不做物理删除；<c>FOFX_RECYCLEONDELETE</c> 保证进回收站可恢复；
/// - <c>IFileOperation</c> 支持长路径与静默执行，避免 SHFileOperation 的 MAX_PATH 限制；
/// - 若 COM 组件在当前环境不可用（例如被安全软件拦截），自动回退到 <c>SHFileOperation</c>（FOF_ALLOWUNDO）。
/// </summary>
public sealed class ShellFileOperations
{
    private const uint FOF_MULTIDESTFILES = 0x0001;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_WANTNUKEWARNING = 0x4000;

    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    private bool _comAvailable = true;

    public bool UsingModernApi => _comAvailable;

    public sealed record OperationResult(bool Success, string Message, bool Aborted = false)
    {
        public static OperationResult Ok(string message = "完成") => new(true, message);
        public static OperationResult Fail(string message) => new(false, message);
        public static OperationResult Cancelled(string message = "已取消") => new(false, message, true);
    }

    /// <summary>把一组文件/目录移入回收站（可恢复）。</summary>
    public OperationResult Recycle(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathTools.Normalize).Distinct().ToList();
        if (list.Count == 0) return OperationResult.Fail("没有需要删除的条目");

        var blocked = list.Where(IsProtectedPath).ToList();
        if (blocked.Count > 0)
            return OperationResult.Fail($"已拦截受保护路径：{PathTools.Shorten(blocked[0])}");

        if (_comAvailable)
        {
            var viaCom = TryRecycleWithCom(list);
            if (viaCom is not null) return viaCom;
        }
        return RecycleWithShellApi(list, delete: true);
    }

    /// <summary>永久删除（仅用于“回收站已满/用户明确选择永久删除”的场景，需二次确认）。</summary>
    public OperationResult DeletePermanently(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathTools.Normalize).Distinct().ToList();
        if (list.Count == 0) return OperationResult.Fail("没有需要删除的条目");
        var blocked = list.Where(IsProtectedPath).ToList();
        if (blocked.Count > 0)
            return OperationResult.Fail($"已拦截受保护路径：{PathTools.Shorten(blocked[0])}");
        return RecycleWithShellApi(list, delete: true, allowUndo: false);
    }

    /// <summary>移动文件/目录到目标目录。</summary>
    public OperationResult Move(IEnumerable<string> sources, string targetDirectory)
    {
        var list = sources.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathTools.Normalize).Distinct().ToList();
        if (list.Count == 0) return OperationResult.Fail("没有需要移动的条目");

        var target = PathTools.Normalize(targetDirectory);
        if (!Directory.Exists(target)) return OperationResult.Fail($"目标目录不存在：{target}");

        foreach (var src in list)
        {
            if (string.Equals(PathTools.Key(src), PathTools.Key(target), StringComparison.Ordinal))
                return OperationResult.Fail("源与目标相同，无需移动");
            if (PathTools.Key(target).StartsWith(PathTools.Key(src) + "\\", StringComparison.Ordinal))
                return OperationResult.Fail($"不能把目录移动到它自己的子目录：{PathTools.Shorten(src)}");
        }

        if (_comAvailable)
        {
            var viaCom = TryMoveWithCom(list, target);
            if (viaCom is not null) return viaCom;
        }
        return MoveWithShellApi(list, target);
    }

    /// <summary>重命名（同目录改名）。</summary>
    public OperationResult Rename(string path, string newName)
    {
        var src = PathTools.Normalize(path);
        var safe = PathTools.SanitizeFileName(newName);
        if (string.IsNullOrWhiteSpace(safe)) return OperationResult.Fail("新名称为空");
        var dir = Path.GetDirectoryName(src);
        if (string.IsNullOrEmpty(dir)) return OperationResult.Fail("无法重命名卷根");

        var dst = Path.Combine(dir, safe);
        if (PathTools.Key(src) == PathTools.Key(dst)) return OperationResult.Fail("名称未变化");
        if (File.Exists(dst) || Directory.Exists(dst)) return OperationResult.Fail($"目标名称已存在：{safe}");

        try
        {
            if (Directory.Exists(src)) Directory.Move(src, dst);
            else File.Move(src, dst);
            return OperationResult.Ok($"已重命名为 {safe}");
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail("权限不足或文件被占用，需以管理员身份运行后重试");
        }
        catch (IOException ex)
        {
            return OperationResult.Fail($"重命名失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"重命名失败：{ex.Message}");
        }
    }

    private static bool IsProtectedPath(string path) => SharedProtection.Value.IsProtected(path);

    private static readonly Lazy<ProtectionEngine> SharedProtection = new(() => new ProtectionEngine());

    // ------------------------------------------------------------------ IFileOperation

    private OperationResult? TryRecycleWithCom(List<string> paths)
    {
        object? comObject = null;
        try
        {
            comObject = new FileOperationComObject();
            var op = (IFileOperation)comObject;
            var hr = op.SetOperationFlags(FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);
            if (hr != 0) return null;

            var sink = new ProgressSink();
            foreach (var path in paths)
            {
                var item = CreateShellItem(path);
                if (item is null) return OperationResult.Fail($"无法解析路径：{PathTools.Shorten(path)}");
                try
                {
                    hr = op.DeleteItem(item, sink);
                    if (hr != 0) return OperationResult.Fail($"加入删除队列失败（HRESULT 0x{hr:X8}）：{PathTools.Shorten(path)}");
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            hr = op.PerformOperations();
            if (hr != 0)
                return OperationResult.Fail($"回收站操作失败（HRESULT 0x{hr:X8}）。可能原因：目标被占用、权限不足或回收站不可用。");

            var aborted = sink.AnyAborted;
            return aborted
                ? new OperationResult(false, "操作被 Shell 中止（部分条目未删除）", true)
                : OperationResult.Ok($"已将 {paths.Count} 个条目移入回收站");
        }
        catch (COMException)
        {
            _comAvailable = false;
            return null;
        }
        catch (InvalidCastException)
        {
            _comAvailable = false;
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            _comAvailable = false;
            return null;
        }
        catch
        {
            _comAvailable = false;
            return null;
        }
        finally
        {
            if (comObject is not null)
            {
                try { Marshal.FinalReleaseComObject(comObject); } catch { /* 忽略 */ }
            }
        }
    }

    private OperationResult? TryMoveWithCom(List<string> sources, string target)
    {
        object? comObject = null;
        try
        {
            comObject = new FileOperationComObject();
            var op = (IFileOperation)comObject;
            var hr = op.SetOperationFlags(FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR);
            if (hr != 0) return null;

            var sink = new ProgressSink();
            var targetItem = CreateShellItem(target);
            if (targetItem is null) return OperationResult.Fail("无法解析目标目录");
            try
            {
                foreach (var src in sources)
                {
                    var item = CreateShellItem(src);
                    if (item is null) return OperationResult.Fail($"无法解析路径：{PathTools.Shorten(src)}");
                    try
                    {
                        hr = op.MoveItem(item, targetItem, null, sink);
                        if (hr != 0)
                            return OperationResult.Fail($"加入移动队列失败（HRESULT 0x{hr:X8}）：{PathTools.Shorten(src)}");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }

                hr = op.PerformOperations();
                if (hr != 0) return OperationResult.Fail($"移动失败（HRESULT 0x{hr:X8}）");
                return sink.AnyAborted
                    ? new OperationResult(false, "移动被中止", true)
                    : OperationResult.Ok($"已移动 {sources.Count} 个条目");
            }
            finally
            {
                Marshal.ReleaseComObject(targetItem);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            _comAvailable = false;
            return null;
        }
        catch
        {
            _comAvailable = false;
            return null;
        }
        finally
        {
            if (comObject is not null)
            {
                try { Marshal.FinalReleaseComObject(comObject); } catch { /* 忽略 */ }
            }
        }
    }

    private static IShellItem? CreateShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return hr == 0 ? item : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    // ------------------------------------------------------------------ 回退：SHFileOperation

    private OperationResult RecycleWithShellApi(List<string> paths, bool delete, bool allowUndo = true)
    {
        try
        {
            var flags = FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR;
            if (allowUndo) flags |= FOF_ALLOWUNDO;
            if (!allowUndo) flags |= FOF_WANTNUKEWARNING;

            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = delete ? FO_DELETE : FO_MOVE,
                pFrom = string.Join('\0', paths) + "\0\0",
                pTo = null,
                fFlags = (ushort)flags,
            };

            var rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted)
                return OperationResult.Ok(allowUndo
                    ? $"已将 {paths.Count} 个条目移入回收站"
                    : $"已永久删除 {paths.Count} 个条目");

            if (op.fAnyOperationsAborted) return new OperationResult(false, "操作被中止", true);
            return OperationResult.Fail(DescribeShellError(rc));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"调用 Shell 文件操作失败：{ex.Message}");
        }
    }

    private OperationResult MoveWithShellApi(List<string> sources, string target)
    {
        try
        {
            var flags = FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR | FOF_MULTIDESTFILES;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_MOVE,
                pFrom = string.Join('\0', sources) + "\0\0",
                pTo = target + "\0\0",
                fFlags = (ushort)flags,
            };
            var rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted)
                return OperationResult.Ok($"已移动 {sources.Count} 个条目");
            if (op.fAnyOperationsAborted) return new OperationResult(false, "移动被中止", true);
            return OperationResult.Fail(DescribeShellError(rc));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"调用 Shell 文件操作失败：{ex.Message}");
        }
    }

    private static string DescribeShellError(int code) => code switch
    {
        0x71 => "操作被取消",
        0x78 => "目标目录访问被拒绝（权限不足）",
        0x79 => "目标目录访问被拒绝（权限不足）",
        0x7A => "源文件或目标路径无效",
        0x7B => "源文件或目标路径无效",
        0x7C => "路径无效",
        0x7D => "路径无效",
        0x7E => "路径过长",
        0x7F => "路径过长",
        0x80 => "文件被占用或已打开",
        0x81 => "文件被占用或已打开",
        0x82 => "目标已存在",
        0x83 => "目标已存在",
        0x84 => "磁盘空间不足",
        0x85 => "磁盘空间不足",
        0x86 => "回收站不可用或已被禁用",
        0x87 => "回收站不可用或已被禁用",
        0xB7 => "文件名或扩展名过长",
        0x402 => "未知错误",
        _ => $"Shell 文件操作返回错误码 0x{code:X}",
    };

    private const uint FO_MOVE = 0x0001;
    private const uint FO_DELETE = 0x0003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // ------------------------------------------------------------------ COM 接口定义

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationComObject
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        [PreserveSig] int SetProgressDialog(IntPtr popd);
        [PreserveSig] int SetProperties(IntPtr pproparray);
        [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
        [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int DeleteItems(IntPtr punkItems);
        [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName,
            IFileOperationProgressSink? pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>进度回调。默认实现：仅记录是否被中止。</summary>
    private sealed class ProgressSink : IFileOperationProgressSink
    {
        public bool AnyAborted { get; private set; }

        public int StartOperations() => 0;
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) => 0;
        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) => 0;
        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => 0;
        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => 0;
        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => 0;
        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => 0;
        public int PreDeleteItem(uint dwFlags, IShellItem psiItem) => 0;
        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated) => 0;
        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) => 0;
        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => 0;
        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
