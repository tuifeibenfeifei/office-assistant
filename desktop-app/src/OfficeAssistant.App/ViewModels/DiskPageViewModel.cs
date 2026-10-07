using System.Collections.ObjectModel;
using System.Diagnostics;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Cleanup;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Scanning;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>
/// 磁盘管理页视图模型（功能三核心）。
/// 三段式布局的数据源：左侧盘符树、中部文件详情与内容摘要、右侧操作面板；
/// 另含未识别文件区与一键清理清单。
/// </summary>
public sealed class DiskPageViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Stopwatch _progressThrottle = Stopwatch.StartNew();

    private FileNodeViewModel? _selectedNode;
    private string _statusText = "尚未扫描。点击「开始扫描」建立本地索引。";
    private string _progressText = string.Empty;
    private double _progressPercent;
    private bool _isScanning;
    private bool _isBusy;
    private string _indexSummary = "索引为空";
    private string _cleanupSummary = "尚未生成清理清单";
    private CleanupEngine.CleanupPlan? _currentPlan;
    private List<DuplicateGroupViewModel> _duplicateGroups = [];
    private int _selectedCleanupCount;
    private long _selectedCleanupBytes;
    private bool _onlySystemDrive;
    private bool _useIncremental = true;
    private bool _withRecognition = true;

    /// <summary>
    /// 磁盘管理的主视图模型。
    ///
    /// 设计取舍（按用户反馈调整过）：界面**只保留列表 + 一键清理**。
    /// 早期的无限画布（盘方块、分支曲线、拖拽搬运）虽然好看，但拖拽卡顿、
    /// 学习成本高，而用户真正需要的是"能一键把能清的东西清掉"。
    /// 因此画布与文件转移功能已整体移除，清理与删除仍走同一套安全流程。
    /// </summary>
    public DiskPageViewModel(AppHost host)
    {
        _host = host;
        _onlySystemDrive = host.Settings.OnlySystemDrive;
        _useIncremental = host.Settings.IncrementalScan;
        _withRecognition = host.Settings.ScanWithRecognition;

        ScanCommand = new AsyncRelayCommand(() => ScanAsync(_useIncremental), () => !IsScanning);
        FullScanCommand = new AsyncRelayCommand(() => ScanAsync(false), () => !IsScanning);
        CancelScanCommand = new RelayCommand(() => _host.ScanEngine.Cancel(), () => IsScanning);
        RefreshTreeCommand = new AsyncRelayCommand(RefreshTreeAsync, () => !IsScanning);
        BuildCleanupPlanCommand = new AsyncRelayCommand(BuildCleanupPlanAsync, () => !IsBusy);
        ExecuteCleanupCommand = new AsyncRelayCommand(ExecuteCleanupAsync, () => CleanupItems.Any(i => i.Selected));
        LoadUnknownCommand = new AsyncRelayCommand(LoadUnknownAsync, () => !IsBusy);
        FindDuplicatesCommand = new AsyncRelayCommand(FindDuplicatesAsync, () => !IsBusy);


        // 起始提示交给画布：画布初始化后会写入更准确的状态（盘符数量、索引规模）
        _statusText = string.Empty;
    }

    // ------------------------------------------------------------------ 命令

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand FullScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand RefreshTreeCommand { get; }
    public AsyncRelayCommand BuildCleanupPlanCommand { get; }
    public AsyncRelayCommand ExecuteCleanupCommand { get; }
    public AsyncRelayCommand LoadUnknownCommand { get; }
    public AsyncRelayCommand FindDuplicatesCommand { get; }

    /// <summary>由视图注入：请求把一组路径移入回收站（含二次确认）。</summary>
    public Func<IReadOnlyList<string>, string, Task<bool>>? RequestDeleteAsync { get; set; }

    public Action<string>? RequestNotify { get; set; }

    public Action? RequestTreeReload { get; set; }


    // ------------------------------------------------------------------ 属性

    public ObservableCollection<FileNodeViewModel> Roots { get; } = [];

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = [];

    public ObservableCollection<UnknownFileViewModel> UnknownFiles { get; } = [];

    public ObservableCollection<CleanupItemViewModel> CleanupItems { get; } = [];

    public ObservableCollection<DuplicateGroupViewModel> DuplicateGroups { get; } = [];

    // ------------------------------------------------------------------ 清理与预览（原画布职责，已并入本页）

    /// <summary>待判断文件的抽屉行。</summary>
    public ObservableCollection<UnknownRowViewModel> UnknownRows { get; } = [];

    /// <summary>一键清理预览的分类行。</summary>
    public ObservableCollection<CleanupGroupViewModel> CleanupGroups { get; } = [];

    /// <summary>深度清理（百分百可清理）预览的行。</summary>
    public ObservableCollection<TrustedRowViewModel> TrustedRows { get; } = [];

    public bool IsUnknownDrawerOpen
    {
        get => _isUnknownDrawerOpen;
        set => SetProperty(ref _isUnknownDrawerOpen, value);
    }

    private bool _isUnknownDrawerOpen;

    public bool IsCleanupPreviewOpen
    {
        get => _isCleanupPreviewOpen;
        set => SetProperty(ref _isCleanupPreviewOpen, value);
    }

    private bool _isCleanupPreviewOpen;

    public string CleanupPreviewSummary
    {
        get => _cleanupPreviewSummary;
        private set => SetProperty(ref _cleanupPreviewSummary, value);
    }

    private string _cleanupPreviewSummary = string.Empty;

    public bool IsTrustedPreviewOpen
    {
        get => _isTrustedPreviewOpen;
        set => SetProperty(ref _isTrustedPreviewOpen, value);
    }

    private bool _isTrustedPreviewOpen;

    public string TrustedPreviewSummary
    {
        get => _trustedPreviewSummary;
        private set => SetProperty(ref _trustedPreviewSummary, value);
    }

    private string _trustedPreviewSummary = string.Empty;

    /// <summary>一键清理匹配进行中：界面据此禁用按钮，避免重复触发。</summary>
    public bool IsCleanupBusy
    {
        get => _isCleanupBusy;
        private set => SetProperty(ref _isCleanupBusy, value);
    }

    private bool _isCleanupBusy;

    public bool IsTrustedBusy
    {
        get => _isTrustedBusy;
        private set => SetProperty(ref _isTrustedBusy, value);
    }

    private bool _isTrustedBusy;

    public string UnknownCountText => UnknownRows.Count == 0 ? "0" : UnknownRows.Count.ToString("N0");

    // ------------------------------------------------------------------ 主路径：目录树 + 批量操作

    /// <summary>已勾选的节点（用于批量删除）。</summary>
    public ObservableCollection<FileNodeViewModel> CheckedNodes { get; } = [];

    public string BatchSummary
    {
        get => _batchSummary;
        private set => SetProperty(ref _batchSummary, value);
    }

    private string _batchSummary = "尚未勾选任何条目。";

    /// <summary>当前选中条目的详情（列表界面右侧显示）。</summary>
    public string SelectedDetailText
    {
        get => _selectedDetailText;
        set => SetProperty(ref _selectedDetailText, value);
    }

    private string _selectedDetailText = string.Empty;

    /// <summary>勾选/取消勾选一个节点，并刷新批量操作摘要。</summary>
    public void ToggleCheck(FileNodeViewModel node)
    {
        node.IsChecked = !node.IsChecked;
        SyncChecked();
    }

    /// <summary>把树的勾选状态同步到批量列表（递归遍历已加载的节点）。</summary>
    public void SyncChecked()
    {
        CheckedNodes.Clear();
        foreach (var root in Roots) CollectChecked(root);

        var bytes = CheckedNodes.Where(n => n.IsFolder).Sum(n => n.SubtreeSize > 0 ? n.SubtreeSize : n.Size);
        BatchSummary = CheckedNodes.Count == 0
            ? "尚未勾选任何条目。"
            : $"已勾选 {CheckedNodes.Count} 项" +
              (bytes > 0 ? $"，合计约 {Format.Bytes(bytes)}" : string.Empty) +
              "。删除会移入回收站（可恢复）。";
        OnPropertyChanged(nameof(BatchSummary));
    }

    private void CollectChecked(FileNodeViewModel node)
    {
        if (node.IsPlaceholder) return;
        if (node.IsChecked) CheckedNodes.Add(node);
        foreach (var child in node.Children) CollectChecked(child);
    }

    /// <summary>批量删除已勾选的条目（复用统一的删除流程：防护 → 确认 → 回收站）。</summary>
    public async Task<bool> DeleteCheckedAsync()
    {
        var paths = CheckedNodes.Where(n => !n.IsPlaceholder).Select(n => n.Path).Distinct().ToList();
        if (paths.Count == 0)
        {
            StatusText = "请先勾选要删除的条目。";
            return false;
        }

        if (RequestDeleteAsync is null)
        {
            StatusText = "删除入口不可用。";
            return false;
        }

        var ok = await RequestDeleteAsync(paths, $"删除已勾选的 {paths.Count} 个条目").ConfigureAwait(true);
        if (ok)
        {
            InvalidateCaches(paths);
            await RefreshTreeAsync().ConfigureAwait(true);
        }

        return ok;
    }

    /// <summary>删除后刷新索引读数，避免界面显示过期数据。</summary>
    private void InvalidateCaches(IReadOnlyList<string> paths)
    {
        _ = RefreshIndexSummaryAsync();
        Log.Info($"批量删除后刷新索引读数：{paths.Count} 条路径");
    }

    // ------------------------------------------------------------------ 一键清理（预览 → 确认 → 回收站）

    /// <summary>生成一键清理预览：按规则匹配安全级内容，分类列出数量与可释放空间。</summary>
    public async Task BuildCleanupPreviewAsync()
    {
        // 防重入：匹配要遍历全部安全级规则，重复点击会叠加多次查询
        if (IsCleanupBusy) return;
        IsCleanupBusy = true;
        StatusText = "正在匹配安全级清理规则…";

        try
        {
            await BuildCleanupPlanAsync().ConfigureAwait(true);

            CleanupGroups.Clear();
            var groups = CleanupItems
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Category) ? "其他" : item.Category)
                .OrderByDescending(g => g.Sum(i => i.Size));

            foreach (var group in groups)
                CleanupGroups.Add(new CleanupGroupViewModel(group.Key, group.Count(), group.Sum(i => i.Size)));

            var total = CleanupGroups.Sum(g => g.Bytes);
            CleanupPreviewSummary = CleanupGroups.Count == 0
                ? "没有找到可安全删除的内容。"
                : $"共 {CleanupGroups.Sum(g => g.Count):N0} 项，预计可释放 {Format.Bytes(total)}";
            IsCleanupPreviewOpen = true;
        }
        catch (Exception ex)
        {
            StatusText = $"生成清理预览失败：{ex.Message}";
        }
        finally
        {
            IsCleanupBusy = false;
        }
    }

    /// <summary>确认执行一键清理。</summary>
    public async Task ConfirmCleanupAsync()
    {
        IsCleanupPreviewOpen = false;
        try
        {
            await ExecuteCleanupAsync().ConfigureAwait(true);
            StatusText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"清理失败：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------ 深度清理（百分百可清理）

    /// <summary>扫描可信目标并打开预览（只统计，不删除）。</summary>
    public async Task BuildTrustedPreviewAsync()
    {
        if (IsTrustedBusy) return;
        IsTrustedBusy = true;
        StatusText = "正在统计可清理的临时文件…";

        try
        {
            var engine = new TrustedCleanupEngine(_host.Database, _host.OperationExecutor, _host.Protection);
            var results = await Task.Run(() => engine.Scan()).ConfigureAwait(true);

            TrustedRows.Clear();
            foreach (var row in results.Where(r => r.Exists && r.FileCount > 0).OrderByDescending(r => r.Bytes))
                TrustedRows.Add(new TrustedRowViewModel(row));

            var totalBytes = results.Sum(r => r.Bytes);
            var totalFiles = results.Sum(r => r.FileCount);
            TrustedPreviewSummary = totalFiles == 0
                ? "临时目录已经很干净，没有需要清理的内容。"
                : $"共 {totalFiles:N0} 个临时文件，可释放 {Format.Bytes(totalBytes)}。这些内容删除后由程序自动重建。";
            IsTrustedPreviewOpen = true;
        }
        catch (Exception ex)
        {
            StatusText = $"统计失败：{ex.Message}";
        }
        finally
        {
            IsTrustedBusy = false;
        }
    }

    /// <summary>执行深度清理（走回收站，可恢复）。</summary>
    public async Task ConfirmTrustedCleanupAsync()
    {
        if (IsTrustedBusy) return;
        IsTrustedBusy = true;
        IsTrustedPreviewOpen = false;
        StatusText = "正在清理临时文件…";

        try
        {
            var engine = new TrustedCleanupEngine(_host.Database, _host.OperationExecutor, _host.Protection);
            var report = await Task.Run(() => engine.Execute()).ConfigureAwait(true);

            StatusText = report.Summary;
            await RefreshIndexSummaryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"清理失败：{ex.Message}";
        }
        finally
        {
            IsTrustedBusy = false;
        }
    }

    // ------------------------------------------------------------------ 待判断文件

    /// <summary>打开"待判断文件"抽屉：无法识别内容的文件集中在这里由用户决定。</summary>
    public async Task OpenUnknownDrawerAsync()
    {
        IsUnknownDrawerOpen = true;
        UnknownRows.Clear();
        try
        {
            await LoadUnknownAsync().ConfigureAwait(true);
            foreach (var item in UnknownFiles) UnknownRows.Add(new UnknownRowViewModel(item));
            OnPropertyChanged(nameof(UnknownCountText));
        }
        catch (Exception ex)
        {
            StatusText = $"读取待判断文件失败：{ex.Message}";
        }
    }

    public void CloseUnknownDrawer() => IsUnknownDrawerOpen = false;

    /// <summary>抽屉内的批量操作：保留 / 删除。删除走统一安全流程（防护 → 确认 → 回收站）。</summary>
    public async Task ApplyUnknownActionAsync(string action)
    {
        var selected = UnknownRows.Where(r => r.Selected).Select(r => r.Path).ToList();
        if (selected.Count == 0)
        {
            StatusText = "请先勾选要处理的文件。";
            return;
        }

        switch (action)
        {
            case "keep":
                foreach (var row in UnknownRows.Where(r => r.Selected)) row.Selected = false;
                StatusText = $"已保留 {selected.Count} 个文件（不再出现在待判断列表）。";
                break;

            case "delete":
                if (RequestDeleteAsync is null)
                {
                    StatusText = "删除入口不可用。";
                    return;
                }

                await RequestDeleteAsync(selected, "删除待判断文件").ConfigureAwait(true);
                foreach (var row in UnknownRows.Where(r => r.Selected).ToList()) UnknownRows.Remove(row);
                OnPropertyChanged(nameof(UnknownCountText));
                break;
        }
    }

    public FileNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedPath));
            OnPropertyChanged(nameof(SelectedSizeText));
            OnPropertyChanged(nameof(SelectedTypeText));
            OnPropertyChanged(nameof(SelectedMtimeText));
            OnPropertyChanged(nameof(SelectedSummary));
            OnPropertyChanged(nameof(SelectedRiskText));
            OnPropertyChanged(nameof(SelectedThumbnail));
            OnPropertyChanged(nameof(SelectedMagic));
            OnPropertyChanged(nameof(SelectedChildInfo));
        }
    }

    public bool HasSelection => SelectedNode is not null && !SelectedNode.IsPlaceholder;

    public string SelectedName => SelectedNode?.Name ?? "—";

    public string SelectedPath => SelectedNode?.Path ?? "—";

    public string SelectedSizeText => SelectedNode is null ? "—" : SelectedNode.SizeText;

    public string SelectedTypeText => SelectedNode?.TypeText ?? "—";

    public string SelectedMtimeText => SelectedNode is null || SelectedNode.Mtime <= 0
        ? "—"
        : DateTimeOffset.FromUnixTimeSeconds(SelectedNode.Mtime).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string SelectedRiskText => SelectedNode is null ? "—" : SelectedNode.Risk switch
    {
        RiskLevel.Safe => "安全（可清理）",
        RiskLevel.Caution => "谨慎（需人工确认）",
        _ => "危险（系统/程序目录，已保护）",
    };

    public string SelectedChildInfo => SelectedNode is null
        ? "—"
        : SelectedNode.IsDirectory ? $"{SelectedNode.Children.Count(v => !v.IsPlaceholder)} 个直接子项（展开后统计）" : "文件";

    /// <summary>内容摘要：文本标题/摘录、图片/音视频元信息等。</summary>
    public string SelectedSummary
    {
        get
        {
            if (SelectedNode is null) return "未选择任何条目。";
            if (SelectedNode.IsDirectory) return "文件夹：展开左侧树可查看其中内容。";
            if (!SelectedNode.Recognized)
                return "内容无法识别：已归入「未识别文件」页签，请自行判断后决定保留或清理。\n" +
                       $"文件头（magic bytes）：{SelectedNode.Node.Name}";
            var entry = _host.Database.GetFile(SelectedNode.Path);
            return entry?.Summary ?? "（尚未提取内容摘要，可执行一次带识别的扫描）";
        }
    }

    public string? SelectedThumbnail
    {
        get
        {
            if (SelectedNode is null || SelectedNode.IsDirectory) return null;
            var entry = _host.Database.GetFile(SelectedNode.Path);
            if (entry is null || entry.Category != FileCategory.Image) return null;
            var cache = Path.Combine(AppPaths.CacheDirectory,
                Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(SelectedNode.Path.ToUpperInvariant()))).ToLowerInvariant() + ".png");
            return File.Exists(cache) ? cache : null;
        }
    }

    public string SelectedMagic
    {
        get
        {
            if (SelectedNode is null || SelectedNode.IsDirectory) return string.Empty;
            var entry = _host.Database.GetFile(SelectedNode.Path);
            return string.IsNullOrWhiteSpace(entry?.MagicHex) ? string.Empty : $"文件头：{entry!.MagicHex}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            ScanCommand.RaiseCanExecuteChanged();
            FullScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            RefreshTreeCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            BuildCleanupPlanCommand.RaiseCanExecuteChanged();
            LoadUnknownCommand.RaiseCanExecuteChanged();
            FindDuplicatesCommand.RaiseCanExecuteChanged();
            ExecuteCleanupCommand.RaiseCanExecuteChanged();
        }
    }

    public string IndexSummary
    {
        get => _indexSummary;
        private set => SetProperty(ref _indexSummary, value);
    }

    public string CleanupSummary
    {
        get => _cleanupSummary;
        private set => SetProperty(ref _cleanupSummary, value);
    }

    public string DuplicateSummary { get; private set; } = "尚未执行重复文件分析（属于「谨慎级」，只提示不自动删除）";

    public bool OnlySystemDrive
    {
        get => _onlySystemDrive;
        set
        {
            if (!SetProperty(ref _onlySystemDrive, value)) return;
            _host.Settings.OnlySystemDrive = value;
        }
    }

    public bool UseIncremental
    {
        get => _useIncremental;
        set
        {
            if (!SetProperty(ref _useIncremental, value)) return;
            _host.Settings.IncrementalScan = value;
        }
    }

    public bool WithRecognition
    {
        get => _withRecognition;
        set
        {
            if (!SetProperty(ref _withRecognition, value)) return;
            _host.Settings.ScanWithRecognition = value;
        }
    }

    public int SelectedCleanupCount
    {
        get => _selectedCleanupCount;
        private set => SetProperty(ref _selectedCleanupCount, value);
    }

    public long SelectedCleanupBytes
    {
        get => _selectedCleanupBytes;
        private set
        {
            if (!SetProperty(ref _selectedCleanupBytes, value)) return;
            OnPropertyChanged(nameof(SelectedCleanupText));
        }
    }

    public string SelectedCleanupText => $"已选 {SelectedCleanupCount} 项，预计释放 {Format.Bytes(SelectedCleanupBytes)}";

    // ------------------------------------------------------------------ 初始化

    public async Task InitializeAsync()
    {
        await LoadVolumesAsync().ConfigureAwait(true);
        await RefreshIndexSummaryAsync().ConfigureAwait(true);
        await RefreshTreeAsync().ConfigureAwait(true);
    }

    private async Task LoadVolumesAsync()
    {
        var volumes = await Task.Run(ScanEngine.ListVolumes).ConfigureAwait(true);
        Volumes.Clear();
        foreach (var volume in volumes)
        {
            var item = new VolumeItemViewModel(volume)
            {
                Selected = !OnlySystemDrive || IsSystemDrive(volume.Root),
            };
            item.SelectionChanged += () => { };
            Volumes.Add(item);
        }
    }

    private static bool IsSystemDrive(string root)
        => string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 扫描

    public async Task ScanAsync(bool incremental)
    {
        if (IsScanning) return;
        IsScanning = true;
        ProgressPercent = 0;
        StatusText = incremental ? "正在执行增量扫描…" : "正在执行全盘扫描（只读，不会修改任何文件）…";

        try
        {
            var roots = OnlySystemDrive
                ? Volumes.Where(v => v.Selected && v.Scannable && IsSystemDrive(v.Root)).Select(v => v.Root).ToList()
                : Volumes.Where(v => v.Selected && v.Scannable).Select(v => v.Root).ToList();

            if (roots.Count == 0)
            {
                // 未勾选任何卷时按全部固定磁盘处理
                roots = Volumes.Where(v => v.Scannable).Select(v => v.Root).ToList();
            }

            var options = new ScanOptions
            {
                Mode = incremental ? ScanMode.Incremental : ScanMode.Full,
                Roots = roots,
                MaxParallelism = _host.Settings.ScanParallelism,
                EnableRecognition = WithRecognition,
            };

            var progress = new Progress<ScanProgress>(OnScanProgress);
            var result = await _host.ScanEngine.ScanAsync(options, p => ((IProgress<ScanProgress>)progress).Report(p))
                .ConfigureAwait(true);

            StatusText = result.Summary;
            await RefreshIndexSummaryAsync().ConfigureAwait(true);
            RequestTreeReload?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("扫描失败", ex);
            StatusText = $"扫描失败：{ex.Message}";
            RequestNotify?.Invoke($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsScanning = false;
            ProgressPercent = 0;
            ProgressText = string.Empty;
        }
    }

    private void OnScanProgress(ScanProgress progress)
    {
        // 进度事件可能非常密集，这里做 250ms 节流，避免拖慢 UI 线程
        if (_progressThrottle.ElapsedMilliseconds < 250) return;
        _progressThrottle.Restart();

        ProgressText = $"已遍历 {progress.FilesSeen + progress.DirsSeen:N0} 项（文件 {progress.FilesSeen:N0} / 目录 {progress.DirsSeen:N0}），" +
                       $"累计 {Format.Bytes(progress.BytesSeen)}，{progress.FilesPerSecond:N0} 项/秒，" +
                       $"已用时 {Format.Duration(progress.ElapsedSeconds)}";
        ProgressPercent = Math.Min(99, progress.ElapsedSeconds * 2);
    }

    private async Task RefreshIndexSummaryAsync()
    {
        var summary = await Task.Run(() =>
        {
            var files = _host.Database.CountFiles();
            var dirs = _host.Database.CountDirs();
            var bytes = _host.Database.TotalBytes();
            var recognized = _host.Database.RecognizedFiles();
            var unknown = _host.Database.CountUnrecognized();
            var coverage = files == 0 ? 0 : (double)recognized / files * 100.0;
            return $"索引：文件 {files:N0} 个，目录 {dirs:N0} 个，合计 {Format.Bytes(bytes)}；" +
                   $"识别覆盖率 {coverage:0.#}%（未识别 {unknown:N0} 个）";
        }).ConfigureAwait(true);

        IndexSummary = summary;
    }

    public async Task RefreshTreeAsync()
    {
        try
        {
            var volumes = Volumes.Where(v => v.Scannable).ToList();
            var roots = await Task.Run(() =>
            {
                var list = new List<FileTreeNode>();
                foreach (var volume in volumes)
                {
                    var children = _host.Database.CountChildren(volume.Root);
                    list.Add(new FileTreeNode
                    {
                        Path = volume.Root,
                        Name = $"{volume.Root}  {volume.Label}",
                        Kind = EntryKind.Volume,
                        Size = volume.UsedBytes,
                        SubtreeSize = volume.UsedBytes,
                        HasChildren = children > 0,
                        ChildCount = (int)Math.Min(int.MaxValue, children),
                        Recognized = true,
                        Category = FileCategory.Other,
                        Risk = RiskLevel.Dangerous,
                    });
                }
                return list;
            }).ConfigureAwait(true);

            Roots.Clear();
            foreach (var root in roots)
            {
                var node = new FileNodeViewModel(_host, root);
                if (root.HasChildren) node.IsExpanded = Roots.Count == 0;
                Roots.Add(node);
            }

            if (Roots.Count == 0)
                StatusText = "未发现可扫描的固定磁盘。";
        }
        catch (Exception ex)
        {
            Log.Error("刷新盘符树失败", ex);
        }
    }

    // ------------------------------------------------------------------ 未识别文件

    public async Task LoadUnknownAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "正在读取未识别文件清单…";
            var items = await Task.Run(() => _host.Database.FindUnrecognized(3000)).ConfigureAwait(true);
            UnknownFiles.Clear();
            foreach (var entry in items) UnknownFiles.Add(new UnknownFileViewModel(entry));
            StatusText = UnknownFiles.Count == 0
                ? "未识别文件区为空（可能尚未扫描，或识别覆盖率已覆盖全部文件）。"
                : $"未识别文件 {UnknownFiles.Count} 个（按体积降序）。这些文件的内容无法判定，请人工决定保留、删除或移动。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>对未识别文件执行批量删除（仍然走误删防护与回收站）。</summary>
    public async Task DeleteSelectedUnknownAsync()
    {
        var paths = UnknownFiles.Where(f => f.Selected).Select(f => f.Path).ToList();
        if (paths.Count == 0)
        {
            RequestNotify?.Invoke("请先勾选需要处理的未识别文件。");
            return;
        }
        if (RequestDeleteAsync is null) return;

        var ok = await RequestDeleteAsync(paths, "删除未识别文件").ConfigureAwait(true);
        if (!ok) return;

        foreach (var path in paths)
        {
            var item = UnknownFiles.FirstOrDefault(f => f.Path == path);
            if (item is not null) UnknownFiles.Remove(item);
        }
        StatusText = $"已处理 {paths.Count} 个未识别文件（移入回收站）。";
        await RefreshIndexSummaryAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ 一键清理

    public async Task BuildCleanupPlanAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "正在匹配安全级清理规则…";
            var plan = await Task.Run(() => _host.CleanupEngine.BuildPlan(msg => RequestNotify?.Invoke(msg)))
                .ConfigureAwait(true);

            _currentPlan = plan;
            CleanupItems.Clear();
            foreach (var candidate in plan.Items)
            {
                var vm = new CleanupItemViewModel(candidate);
                vm.SelectionChanged += RecalculateCleanupSelection;
                CleanupItems.Add(vm);
            }
            RecalculateCleanupSelection();

            CleanupSummary = plan.Items.Count == 0
                ? string.Join("；", plan.Warnings.DefaultIfEmpty("未发现可安全清理的文件。"))
                : $"预演完成：{plan.EstimateText}；规则匹配耗时 {Format.Duration(plan.ScanSeconds)}。" +
                  (plan.Warnings.Count > 0 ? " " + string.Join("；", plan.Warnings) : string.Empty);

            StatusText = CleanupSummary;
            ExecuteCleanupCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RecalculateCleanupSelection()
    {
        SelectedCleanupCount = CleanupItems.Count(i => i.Selected);
        SelectedCleanupBytes = CleanupItems.Where(i => i.Selected).Sum(i => i.Size);
        ExecuteCleanupCommand.RaiseCanExecuteChanged();
    }

    public void SelectAllCleanup(bool selected)
    {
        foreach (var item in CleanupItems) item.Selected = selected;
        RecalculateCleanupSelection();
    }

    /// <summary>执行一键清理（把选中项移入回收站）。画布与页签共用同一实现。</summary>
    public async Task ExecuteCleanupAsync()
    {
        var selections = CleanupItems.Where(i => i.Selected).Select(i => i.Candidate).ToList();
        if (selections.Count == 0) return;

        if (RequestDeleteAsync is null) return;
        var confirmed = await RequestDeleteAsync(selections.Select(s => s.Path).ToList(),
            $"一键清理（{selections.Count} 项，预计释放 {Format.Bytes(selections.Sum(s => s.Size))}）").ConfigureAwait(true);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            StatusText = "正在把清理项移入回收站…";
            var report = await Task.Run(() => _host.CleanupEngine.Execute(selections)).ConfigureAwait(true);
            StatusText = report.Summary;
            RequestNotify?.Invoke(report.Summary);

            await BuildCleanupPlanAsync().ConfigureAwait(true);
            await RefreshIndexSummaryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 重复文件（谨慎级）

    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "正在分析重复文件（先按大小分组，再计算 MD5 确认）…";
            var progress = new Progress<string>(msg => StatusText = msg);
            var groups = await Task.Run(() => _host.CleanupEngine.FindDuplicatesParallel(
                1024 * 1024, 400, progress, CancellationToken.None)).ConfigureAwait(true);

            DuplicateGroups.Clear();
            foreach (var group in groups)
            {
                var vm = new DuplicateGroupViewModel(group);
                vm.SelectionChanged += RecalculateCleanupSelection;
                DuplicateGroups.Add(vm);
            }

            var reclaimable = groups.Sum(g => g.Size * (g.Files.Count - 1));
            DuplicateSummary = groups.Count == 0
                ? "未发现重复文件（要求同名内容一致且大于 1MB）。"
                : $"发现 {groups.Count} 组重复文件，理论上可释放 {Format.Bytes(reclaimable)}。" +
                  "重复文件属于「谨慎级」，请人工确认后再删除；每组默认保留最早修改的一份。";
            StatusText = DuplicateSummary;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteSelectedDuplicatesAsync()
    {
        var paths = DuplicateGroups.SelectMany(g => g.Items)
            .Where(i => i.Selected && !i.IsKeeper)
            .Select(i => i.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            RequestNotify?.Invoke("没有勾选需要删除的重复副本。");
            return;
        }
        if (RequestDeleteAsync is null) return;

        var ok = await RequestDeleteAsync(paths, $"删除重复文件副本（{paths.Count} 项）").ConfigureAwait(true);
        if (!ok) return;

        StatusText = $"已把 {paths.Count} 个重复副本移入回收站。";
        await FindDuplicatesAsync().ConfigureAwait(true);
        await RefreshIndexSummaryAsync().ConfigureAwait(true);
    }

    /// <summary>磁盘总览：按大类统计占用，供总览区展示。</summary>
    public async Task<string> BuildCategoryUsageTextAsync(string? volumeRoot = null)
    {
        var rows = await Task.Run(() => _host.Database.CategoryUsage(volumeRoot)).ConfigureAwait(true);
        if (rows.Count == 0) return "暂无统计数据，请先扫描。";
        var total = rows.Sum(r => r.Bytes);
        return string.Join("　", rows.Take(8).Select(r =>
            $"{Infrastructure.RecognitionText.Describe(r.Category)} {Format.Bytes(r.Bytes)}（{(total == 0 ? 0 : (double)r.Bytes / total * 100):0.#}%）"));
    }
}

/// <summary>卷（盘符）选择项。</summary>
public sealed class VolumeItemViewModel : ObservableObject
{
    private bool _selected = true;

    public VolumeItemViewModel(VolumeInfo info) => Info = info;

    public VolumeInfo Info { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) SelectionChanged?.Invoke();
        }
    }

    public string Root => Info.Root;
    public string Label => string.IsNullOrWhiteSpace(Info.Label) ? Info.DriveType : Info.Label;
    public bool Scannable => Info.Scannable;
    public long UsedBytes => Info.UsedBytes;
    public string UsedText => $"{Format.Bytes(Info.UsedBytes)} / {Format.Bytes(Info.TotalBytes)}";
    public string FreeText => $"可用 {Format.Bytes(Info.FreeBytes)}";
    public double UsedPercent => Math.Clamp(Info.UsedPercent, 0, 100);
    public string Summary => $"{Info.DriveType} {Info.FileSystem}";

    public event Action? SelectionChanged;
}

/// <summary>一组重复文件。</summary>
public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(CleanupEngine.DuplicateGroup group)
    {
        Items = group.Files.Select((f, index) =>
        {
            var vm = new DuplicateItemViewModel(f, index == 0, group.Hash, group.Size);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DuplicateItemViewModel.Selected)) SelectionChanged?.Invoke();
            };
            return vm;
        }).ToList();
    }

    public List<DuplicateItemViewModel> Items { get; }

    public string Hash => Items.Count > 0 ? Items[0].Hash : string.Empty;
    public string SizeText => Items.Count > 0 ? Format.Bytes(Items[0].GroupSize) : "—";
    public int Count => Items.Count;
    public string Title => $"{SizeText} × {Count} 份 · MD5 {Hash[..Math.Min(12, Hash.Length)]}…";

    public event Action? SelectionChanged;
}

/// <summary>"待判断文件"抽屉里的一行。</summary>
public sealed class UnknownRowViewModel
{
    public UnknownRowViewModel(UnknownFileViewModel source) => Source = source;

    public UnknownFileViewModel Source { get; }

    public string Name => Source.Name;

    public string Path => Source.Path;

    public string SizeText => Source.SizeText;

    public string Folder => Source.Folder;

    public bool Selected
    {
        get => Source.Selected;
        set => Source.Selected = value;
    }
}

/// <summary>一键清理预览里的一类。</summary>
public sealed class CleanupGroupViewModel
{
    public CleanupGroupViewModel(string category, int count, long bytes)
    {
        Category = category;
        Count = count;
        Bytes = bytes;
    }

    public string Category { get; }

    public int Count { get; }

    public long Bytes { get; }

    public string CountText => $"{Count:N0} 项";

    public string SizeText => Format.Bytes(Bytes);
}

/// <summary>深度清理预览里的一行：位置、占用，以及"为什么删了没事"。</summary>
public sealed class TrustedRowViewModel
{
    public TrustedRowViewModel(OfficeAssistant.Core.Cleanup.TrustedTargetResult source) => Source = source;

    public OfficeAssistant.Core.Cleanup.TrustedTargetResult Source { get; }

    public string Name => Source.Name;

    public string PathText => Source.Path;

    /// <summary>为什么删了没事——预览窗必须让用户看懂这一点。</summary>
    public string WhySafe => Source.WhySafe;

    public string SizeText => Source.Bytes > 0 ? Format.Bytes(Source.Bytes) : "—";

    public string CountText => $"{Source.FileCount:N0} 个文件";

    public string Status => Source.Status;
}
