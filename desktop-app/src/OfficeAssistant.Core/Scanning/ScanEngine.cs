using System.Collections.Concurrent;
using System.Diagnostics;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Recognition;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Scanning;

public sealed record ScanResult(
    ScanMode Mode,
    long Files,
    long Dirs,
    long Bytes,
    long SkippedSubtrees,
    long AccessDenied,
    long SkippedReparsePoints,
    double Seconds,
    bool Cancelled,
    IReadOnlyList<string> TopErrors)
{
    public double ItemsPerSecond => Seconds <= 0.05 ? 0 : (Files + Dirs) / Seconds;

    public string Summary =>
        $"{(Mode == ScanMode.Full ? "全盘扫描" : "增量扫描")}：文件 {Files:N0}，目录 {Dirs:N0}，共 {Format.Bytes(Bytes)}，" +
        $"耗时 {Format.Duration(Seconds)}（{ItemsPerSecond:N0} 项/秒）" +
        (SkippedSubtrees > 0 ? $"，跳过未变化目录 {SkippedSubtrees:N0}" : string.Empty) +
        (AccessDenied > 0 ? $"，无权限 {AccessDenied:N0}" : string.Empty) +
        (Cancelled ? "，已被用户中止" : string.Empty) +
        (TopErrors.Count > 0 ? $"；首个错误：{TopErrors[0]}" : string.Empty);
}

public sealed class ScanOptions
{
    public ScanMode Mode { get; set; } = ScanMode.Full;

    /// <summary>扫描范围（卷根，如 D:\）。为空则扫描所有固定磁盘。</summary>
    public List<string> Roots { get; set; } = [];

    public int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>扫描完成后执行内容识别（写入摘要、图片/音视频元信息）。</summary>
    public bool EnableRecognition { get; set; } = true;

    /// <summary>安全阀：单次扫描的最大条目数，防止极端情况拖垮系统。</summary>
    public long MaxEntries { get; set; } = 8_000_000;

    public bool FollowReparsePoints { get; set; }

    /// <summary>跳过这些目录（用户排除项）。</summary>
    public List<string> ExcludePaths { get; set; } = [];
}

/// <summary>线程安全的扫描计数器。</summary>
internal sealed class ScanCounters
{
    public long Files;
    public long Dirs;
    public long Bytes;
    public long SkippedSubtrees;
    public long AccessDenied;
    public long ReparsePoints;

    public void AddFiles(long n) => Interlocked.Add(ref Files, n);
    public void AddDirs(long n) => Interlocked.Add(ref Dirs, n);
    public void AddBytes(long n) => Interlocked.Add(ref Bytes, n);
    public void AddSkipped(long n) => Interlocked.Add(ref SkippedSubtrees, n);
    public void AddDenied(long n) => Interlocked.Add(ref AccessDenied, n);
    public void AddReparse(long n) => Interlocked.Add(ref ReparsePoints, n);
}

/// <summary>
/// 文件扫描引擎（功能三核心）。
///
/// 首扫：按顶层子树分片，多线程并行遍历（Windows API 直读目录项），纯只读、不修改任何文件；
///       结果批量写入 SQLite（单写事务 + 预编译语句），避免逐条提交的磁盘开销。
/// 增量：目录指纹快照（mtime + 子项数）比对，未变化子树整棵跳过；跳过子树的文件数与字节数
///       采用上次扫描记录的数值参与汇总，因此总量统计仍然准确。
///
/// 性能目标（对应验收）：约 100 万文件全盘首扫 ≤ 10 分钟；增量 ≤ 30 秒。
/// </summary>
public sealed class ScanEngine
{
    private readonly Database _db;
    private readonly ProtectionEngine _protection;
    private readonly RecognitionEngine _recognition;
    private readonly ConcurrentBag<string> _errors = [];
    private CancellationTokenSource? _cts;

    public ScanEngine(Database db, ProtectionEngine protection, RecognitionEngine recognition)
    {
        _db = db;
        _protection = protection;
        _recognition = recognition;
    }

    public ScanProgress Progress { get; } = new();

    public bool IsRunning => Progress.Running;

    /// <summary>上一次识别阶段的统计（供 UI 展示识别覆盖率）。</summary>
    public RecognitionStats LastRecognition { get; private set; } = new();

    public event Action<ScanProgress>? ProgressChanged;

    public void Cancel() => _cts?.Cancel();

    // ------------------------------------------------------------------ 卷信息

    public static List<VolumeInfo> ListVolumes()
    {
        var list = new List<VolumeInfo>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex)
        {
            Log.Error("枚举驱动器失败", ex);
            return list;
        }

        foreach (var d in drives)
        {
            var info = new VolumeInfo { Root = string.Empty, Scannable = false };
            try
            {
                info.Root = d.RootDirectory.FullName;
                info.DriveType = d.DriveType switch
                {
                    DriveType.Fixed => "本地磁盘",
                    DriveType.Removable => "可移动磁盘",
                    DriveType.Network => "网络位置",
                    DriveType.CDRom => "光驱",
                    DriveType.Ram => "内存盘",
                    _ => "未知",
                };

                if (d.DriveType != DriveType.Fixed || !d.IsReady)
                {
                    info.Label = d.IsReady ? SafeLabel(d) : "（未就绪）";
                    list.Add(info);
                    continue;
                }

                info.Label = SafeLabel(d);
                info.FileSystem = SafeFileSystem(d);
                info.TotalBytes = d.TotalSize;
                info.FreeBytes = d.TotalFreeSpace;
                info.Scannable = true;
            }
            catch
            {
                info.Scannable = false;
            }
            list.Add(info);
        }
        return list;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel; }
        catch { return "本地磁盘"; }
    }

    private static string SafeFileSystem(DriveInfo d)
    {
        try { return d.DriveFormat; }
        catch { return string.Empty; }
    }

    // ------------------------------------------------------------------ 扫描主流程

    public Task<ScanResult> ScanAsync(ScanOptions options, Action<ScanProgress>? onProgress = null)
        => Task.Run(() => Scan(options, onProgress));

    public ScanResult Scan(ScanOptions options, Action<ScanProgress>? onProgress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _errors.Clear();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var counters = new ScanCounters();

        var roots = ResolveRoots(options);
        Progress.Mode = options.Mode;
        Progress.Running = true;
        Progress.StartedAt = DateTime.UtcNow;
        Progress.FinishedAt = null;
        Progress.CurrentRoot = roots.Count > 0 ? roots[0] : string.Empty;
        _db.CurrentBatchStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (onProgress is not null) ProgressChanged += onProgress;
        using var ticker = new ProgressTicker(this, counters);
        ticker.Start();

        var cancelled = false;
        try
        {
            var fingerprints = options.Mode == ScanMode.Incremental ? LoadFingerprints() : [];
            var shardPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (options.Mode == ScanMode.Full)
            {
                // 全量首扫：清空选中卷的旧数据，避免残留已删除条目导致的"幽灵文件"
                foreach (var root in roots) _db.ClearVolume(root);
            }

            // 分片登记需要写库，因此先建一个批次写事务（工作线程各自另开批次）
            var buildLock = new object();
            List<string> shards;
            using (var buildWriter = _db.BeginFileBatch())
            {
                shards = BuildShards(roots, options, fingerprints, counters, shardPaths, buildWriter);
            }

            Log.Info($"扫描开始：模式={options.Mode}，分片={shards.Count}，卷={string.Join(",", roots)}，并行度={options.MaxParallelism}");

            if (shards.Count > 0) RunWorkers(shards, counters, fingerprints, options, token, shardPaths);

            cancelled = token.IsCancellationRequested;

            // 清理本次未再遇到的历史条目（全量扫描时用于剔除已删除文件）
            if (!cancelled && options.Mode == ScanMode.Full)
            {
                foreach (var root in roots)
                {
                    try { _db.DeleteFilesUnder(root, Progress.StartedAt); }
                    catch (Exception ex) { Log.Warn($"清理陈旧条目失败（{root}）：{ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _errors.Add($"扫描引擎异常 :: {ex.GetType().Name}: {ex.Message}");
            Log.Error("扫描引擎异常", ex);
        }
        finally
        {
            ticker.Stop();
            Progress.FilesSeen = counters.Files;
            Progress.DirsSeen = counters.Dirs;
            Progress.BytesSeen = counters.Bytes;
            Progress.SkippedSubtrees = counters.SkippedSubtrees;
            Progress.AccessDenied = counters.AccessDenied;
            Progress.SkippedReparsePoints = counters.ReparsePoints;
            Progress.Running = false;
            Progress.FinishedAt = DateTime.UtcNow;
            if (onProgress is not null)
            {
                ProgressChanged -= onProgress;
                try { onProgress(Progress); } catch { /* 忽略 UI 异常 */ }
            }
        }

        stopwatch.Stop();
        var result = new ScanResult(options.Mode, counters.Files, counters.Dirs, counters.Bytes,
            counters.SkippedSubtrees, counters.AccessDenied, counters.ReparsePoints,
            stopwatch.Elapsed.TotalSeconds, cancelled, _errors.Take(8).ToList());

        try { _db.AddScanHistory(Progress); } catch (Exception ex) { Log.Warn($"写入扫描历史失败：{ex.Message}"); }
        Log.Info(result.Summary);

        if (options.EnableRecognition && !cancelled)
        {
            try
            {
                LastRecognition = _recognition.RunBatch(_db, token);
                Log.Info($"内容识别完成：{LastRecognition.Summary}");
            }
            catch (Exception ex)
            {
                Log.Error("内容识别阶段失败", ex);
            }
        }

        return result;
    }

    private void RunWorkers(List<string> shards, ScanCounters counters,
        Dictionary<string, DirFingerprint> fingerprints, ScanOptions options, CancellationToken token,
        HashSet<string> shardPaths)
    {
        using var queue = new BlockingCollection<string>(8192);
        var flushLock = new object();
        var workerCount = Math.Clamp(options.MaxParallelism, 1, 32);
        var workers = new List<Thread>(workerCount);

        for (var i = 0; i < workerCount; i++)
        {
            var worker = new Thread(() =>
            {
                Database.FileBatchWriter? writer = null;
                List<FileEntry>? buffer = null;
                var flushCount = 0;
                try
                {
                    writer = _db.BeginFileBatch();
                    buffer = new List<FileEntry>(512);
                    while (!queue.IsCompleted)
                    {
                        if (!queue.TryTake(out var dir, Timeout.Infinite, token)) break;
                        try
                        {
                            // parentChanged = true：分片自身必须被遍历（其指纹可能与上次不同），
                            // 而它的子目录仍可走增量跳过路径。
                            Walk(dir, true, buffer, fingerprints, options, token, counters, flushLock,
                                writer, shardPaths, ref flushCount);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            if (_errors.Count < 300) _errors.Add($"{dir} :: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常中止
                }
                catch (Exception ex)
                {
                    if (_errors.Count < 300) _errors.Add($"工作线程异常 :: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (buffer is { Count: > 0 } && writer is not null)
                        {
                            lock (flushLock)
                            {
                                foreach (var e in buffer) writer.Add(e);
                                buffer.Clear();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_errors.Count < 300) _errors.Add($"收尾写入失败 :: {ex.Message}");
                    }
                    try { writer?.Dispose(); } catch { /* 忽略 */ }
                }
            })
            {
                IsBackground = true,
                Name = $"scan-worker-{i}",
                Priority = ThreadPriority.BelowNormal, // 后台静默执行，避免抢占前台 IO
            };
            workers.Add(worker);
            worker.Start();
        }

        foreach (var shard in shards)
        {
            if (token.IsCancellationRequested) break;
            queue.Add(shard, token);
        }
        queue.CompleteAdding();

        foreach (var w in workers)
        {
            try { w.Join(); } catch { /* 忽略 */ }
        }
    }

    private static List<string> ResolveRoots(ScanOptions options)
    {
        if (options.Roots.Count > 0)
            return options.Roots.Select(PathTools.Normalize).Where(r => r.Length > 0).Distinct().ToList();

        return ListVolumes().Where(v => v.Scannable).Select(v => v.Root).ToList();
    }

    private Dictionary<string, DirFingerprint> LoadFingerprints()
    {
        var map = new Dictionary<string, DirFingerprint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = _db.Connection.Query("SELECT path, mtime, child_count, total_size FROM dir_index;",
                s => new DirFingerprint
                {
                    Path = s.GetString(0),
                    Mtime = s.GetInt64(1),
                    ChildCount = (int)s.GetInt64(2),
                    TotalSize = s.GetInt64(3),
                });
            foreach (var r in rows) map[r.Path] = r;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取目录指纹失败，本次按全量处理：{ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// 构建顶层分片：卷根 + 其下每个一级子目录各成一片，负载天然均衡、并行度充足。
    ///
    /// 每个分片都先登记为目录条目（全量扫描会先清空该卷，因此需要重新登记），
    /// 并记录在 <paramref name="pendingShards"/> 中：遍历卷根时遇到这些目录不再下钻，
    /// 否则一级目录会被"根分片递归遍历一次 + 自身作为分片遍历一次"，文件数翻倍。
    ///
    /// 增量判定不在这里做：目录 mtime 的分辨率与文件系统缓存都可能让"刚发生的变更"读不到，
    /// 因此这里只负责切分与登记，增量比对统一在 <see cref="Walk"/> 内完成。
    /// </summary>
    private List<string> BuildShards(List<string> roots, ScanOptions options,
        Dictionary<string, DirFingerprint> fingerprints, ScanCounters counters,
        HashSet<string> pendingShards, Database.FileBatchWriter batchWriter)
    {
        var shards = new List<string>();
        var pendingEntries = new List<FileEntry>();

        void RegisterDirectory(string path, string summary)
        {
            var key = PathTools.Key(path);
            if (!pendingShards.Add(key)) return;
            shards.Add(path);

            // 注意：这里只构造条目、不直接调用 AppendEntry。
            // 局部函数内调用静态方法在个别编译器版本上出现过参数计数误判（CS7036），
            // 统一由外层循环写入，既不依赖该行为，也让写入点只有一个。
            pendingEntries.Add(new FileEntry
            {
                Path = path,
                Parent = Path.GetDirectoryName(path) ?? string.Empty,
                Name = PathTools.IsRoot(path) ? path : Path.GetFileName(path),
                Kind = EntryKind.Directory,
                Recognized = true,
                Category = FileCategory.Other,
                Volume = PathTools.VolumeRoot(path),
                Risk = RiskLevel.Dangerous, // 卷根/顶层目录一律按最高风险对待，绝不进入清理清单
                Summary = summary,
            });
            counters.AddDirs(1);
        }

        foreach (var root in roots)
        {
            if (options.ExcludePaths.Any(x =>
                    PathTools.Key(root).StartsWith(PathTools.Key(x), StringComparison.Ordinal)))
                continue;

            var childCount = 0;
            var top = FileEnumerator.Enumerate(root, CancellationToken.None);
            if (top is null)
            {
                counters.AddDenied(1);
                if (_errors.Count < 300) _errors.Add($"{root} :: {FileEnumerator.DescribeLastError()}");
                RegisterDirectory(root, "卷根目录（无法枚举：权限不足或设备未就绪）");
                continue;
            }

            foreach (var e in top)
            {
                if (!e.IsDirectory) continue;
                if (e.IsReparsePoint && !options.FollowReparsePoints)
                {
                    counters.AddReparse(1);
                    continue;
                }
                RegisterDirectory(e.FullPath, $"顶层目录 {e.Name}");
                childCount++;
            }

            RegisterDirectory(root, $"卷根目录（顶层 {childCount} 个子目录）");
        }

        // 统一写入：分片登记只在扫描开始前由主线程执行，因此无需额外加锁
        foreach (var entry in pendingEntries) batchWriter.Add(entry);

        return shards;
    }

    /// <summary>深度优先遍历一个子树；所有写入都经 <paramref name="flushLock"/> 串行化。</summary>
    private void Walk(string directory, bool isShardRoot, List<FileEntry> buffer,
        Dictionary<string, DirFingerprint> fingerprints, ScanOptions options, CancellationToken token,
        ScanCounters counters, object flushLock, Database.FileBatchWriter writer,
        HashSet<string> shardPaths, ref int flushCount)
    {
        var stack = new Stack<(string Dir, bool IsShardRoot)>();
        stack.Push((directory, isShardRoot));

        // 本轮被"整棵子树跳过"的目录：父目录再遇到该条目时不再重复计数
        var skippedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) return;
            var (dir, shardRoot) = stack.Pop();
            var dirKey = PathTools.Key(dir);
            if (!visited.Add(dirKey)) continue; // 防止联接/重复路径导致的重复遍历

            var dirMtime = FileEnumerator.GetDirectoryMtime(dir);
            var hasFp = fingerprints.TryGetValue(dir, out var fp);

            // 增量判定：本目录指纹（mtime）与上轮一致 → 整棵子树跳过。
            // 分片根节点必须遍历一次（它的直接子项就是分片集合，需要据此决定是否遍历子树），
            // 但分片根之下的目录仍然走跳过路径，因此增量扫描的实际代价与变化量成正比。
            if (options.Mode == ScanMode.Incremental && !shardRoot && hasFp && fp!.Mtime == dirMtime)
            {
                counters.AddSkipped(1);
                counters.AddFiles(fp.ChildCount);
                counters.AddBytes(fp.TotalSize);
                skippedDirs.Add(dirKey);
                continue;
            }

            var raw = FileEnumerator.Enumerate(dir, token);
            if (raw is null)
            {
                // 目录不可访问：登记为目录条目（保留在树中），但不再递归
                AppendEntry(buffer, new FileEntry
                {
                    Path = dir,
                    Parent = Path.GetDirectoryName(dir) ?? string.Empty,
                    Name = Path.GetFileName(dir),
                    Kind = EntryKind.Directory,
                    Recognized = false,
                    Category = FileCategory.Other,
                    Volume = PathTools.VolumeRoot(dir),
                    Summary = "目录无法访问（权限不足或已被移除）",
                    Risk = RiskLevel.Dangerous,
                }, writer, flushLock);

                counters.AddDenied(1);
                if (_errors.Count < 300) _errors.Add($"{dir} :: {FileEnumerator.DescribeLastError()}");
                continue;
            }

            var volume = PathTools.VolumeRoot(dir);
            long dirBytes = 0;
            var childCount = 0;

            foreach (var e in raw)
            {
                if (e.IsDirectory)
                {
                    if (skippedDirs.Contains(PathTools.Key(e.FullPath)))
                    {
                        // 该子树已按指纹计入统计，这里不重复计数
                        childCount++;
                        continue;
                    }

                    // 该目录已作为独立分片登记（条目也已写入），避免重复遍历与重复计数
                    if (!options.FollowReparsePoints && shardPaths.Contains(PathTools.Key(e.FullPath))) continue;

                    var entry = MakeEntry(e, dir, volume, EntryKind.Directory);
                    if (e.IsReparsePoint && !options.FollowReparsePoints)
                    {
                        counters.AddReparse(1);
                        entry.Risk = RiskLevel.Dangerous;
                        entry.Summary = "重解析点（符号链接/联接），未递归进入";
                    }
                    else
                    {
                        stack.Push((e.FullPath, false));
                    }

                    AppendEntry(buffer, entry, writer, flushLock);
                    counters.AddDirs(1);
                    childCount++;
                }
                else
                {
                    var entry = MakeEntry(e, dir, volume, EntryKind.File);
                    dirBytes += e.Size;
                    AppendEntry(buffer, entry, writer, flushLock);
                    counters.AddFiles(1);
                    counters.AddBytes(e.Size);
                    childCount++;
                }

                if (counters.Files + counters.Dirs > options.MaxEntries)
                {
                    Log.Warn("达到单次扫描条目上限，提前结束扫描");
                    return;
                }
            }

            lock (flushLock)
            {
                try
                {
                    _db.UpsertDirFingerprint(new DirFingerprint
                    {
                        Path = dir,
                        Mtime = dirMtime,
                        ChildCount = childCount,
                        TotalSize = dirBytes,
                    });
                }
                catch (Exception ex)
                {
                    if (_errors.Count < 300) _errors.Add($"{dir} :: 指纹写入失败 {ex.Message}");
                }
            }
        }
    }

    private static void AppendEntry(List<FileEntry> buffer, FileEntry entry,
        Database.FileBatchWriter writer, object appendGate)
    {
        lock (appendGate)
        {
            buffer.Add(entry);
            if (buffer.Count < 512) return;
            foreach (var e in buffer) writer.Add(e);
            buffer.Clear();
        }
    }

    private FileEntry MakeEntry(in RawEntry e, string parent, string volume, EntryKind kind)
    {
        var ext = kind == EntryKind.File ? Path.GetExtension(e.Name).ToLowerInvariant() : string.Empty;
        var risk = kind == EntryKind.File
            ? _protection.EvaluateRisk(e.FullPath, ext, e.Size, ProtectionEngine.IsUserDocumentArea(e.FullPath))
            : (_protection.IsProtected(e.FullPath) ? RiskLevel.Dangerous : RiskLevel.Caution);

        return new FileEntry
        {
            Path = e.FullPath,
            Parent = parent,
            Name = e.Name,
            Ext = ext,
            Kind = kind,
            Size = e.Size,
            Mtime = e.MtimeUnix,
            Risk = risk,
            Recognized = kind == EntryKind.Directory,
            Category = kind == EntryKind.Directory ? FileCategory.Other : FileCategory.Unknown,
            Volume = volume,
        };
    }

    private sealed class ProgressTicker : IDisposable
    {
        private readonly ScanEngine _owner;
        private readonly ScanCounters _counters;
        private readonly CancellationTokenSource _cts = new();
        private Thread? _thread;

        public ProgressTicker(ScanEngine owner, ScanCounters counters)
        {
            _owner = owner;
            _counters = counters;
        }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Publish();
                    try { _cts.Token.WaitHandle.WaitOne(400); } catch { break; }
                }
                Publish();
            }) { IsBackground = true, Name = "scan-progress" };
            _thread.Start();
        }

        private void Publish()
        {
            _owner.Progress.FilesSeen = Interlocked.Read(ref _counters.Files);
            _owner.Progress.DirsSeen = Interlocked.Read(ref _counters.Dirs);
            _owner.Progress.BytesSeen = Interlocked.Read(ref _counters.Bytes);
            _owner.Progress.SkippedSubtrees = Interlocked.Read(ref _counters.SkippedSubtrees);
            _owner.Progress.AccessDenied = Interlocked.Read(ref _counters.AccessDenied);
            _owner.Progress.SkippedReparsePoints = Interlocked.Read(ref _counters.ReparsePoints);
            try { _owner.ProgressChanged?.Invoke(_owner.Progress); } catch { /* 忽略 UI 异常 */ }
        }

        public void Stop()
        {
            _cts.Cancel();
            try { _thread?.Join(1500); } catch { /* 忽略 */ }
        }

        public void Dispose() => _cts.Dispose();
    }
}
