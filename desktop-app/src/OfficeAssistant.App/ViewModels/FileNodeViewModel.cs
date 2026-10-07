using System.Collections.ObjectModel;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Safety;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>
/// 树状图节点（懒加载）。盘符为根节点，目录可展开，文件为叶子节点。
/// 子节点首次展开时才查询数据库，保证"树节点展开响应 ≤ 200ms"。
/// </summary>
public sealed class FileNodeViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _childrenLoaded;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoading;

    public FileNodeViewModel(AppHost host, FileTreeNode node, FileNodeViewModel? parent = null)
    {
        _host = host;
        Node = node;
        Parent = parent;
        Children = [];

        // 目录先放一个占位子节点，让展开箭头可见；展开时再真正加载
        if (node.Kind != EntryKind.File && node.HasChildren)
            Children.Add(CreatePlaceholder());
    }

    public FileTreeNode Node { get; }

    public FileNodeViewModel? Parent { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; }

    public bool IsPlaceholder { get; private init; }

    /// <summary>占位节点（"加载中…"/"（空）"），不参与任何文件操作。</summary>
    public static FileNodeViewModel CreatePlaceholder(string text = "加载中…")
        => new(null!, new FileTreeNode { Name = text, Kind = EntryKind.File }) { IsPlaceholder = true };

    public string Name => Node.Name;

    public string Path => Node.Path;

    public EntryKind Kind => Node.Kind;

    public long Size => Node.Size;

    public long SubtreeSize => Node.SubtreeSize;

    public FileCategory Category => Node.Category;

    public RiskLevel Risk => Node.Risk;

    public bool Recognized => Node.Recognized;

    public long Mtime => Node.Mtime;

    public bool IsDirectory => Node.Kind != EntryKind.File;

    public string SizeText => Node.Kind == EntryKind.File
        ? Format.Bytes(Node.Size)
        : Node.SubtreeSize > 0 ? Format.Bytes(Node.SubtreeSize) + "（含子项）" : Format.Bytes(Node.Size);

    public string TypeText => Node.Kind switch
    {
        EntryKind.Volume => "驱动器",
        EntryKind.Directory => "文件夹",
        _ => RecognitionCategoryText(Node.Category, Node.Recognized),
    };

    private static string RecognitionCategoryText(FileCategory category, bool recognized)
        => !recognized ? "未识别" : Infrastructure.RecognitionText.Describe(category);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) _ = LoadChildrenAsync();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || Kind == EntryKind.File || _host is null) return;
        _childrenLoaded = true;
        IsLoading = true;
        try
        {
            var path = Node.Path;
            var children = await Task.Run(() =>
            {
                // 上限保护：像 C:\Windows 这种目录一次能有上万个子项，
                // 即使开了虚拟化，构造上万个 ViewModel 本身也会让界面停顿数秒。
                // 先给前 ChildDisplayLimit 个，其余由用户按需展开下一级再看。
                var rows = _host.Database.GetChildren(path, ChildDisplayLimit + 1);
                return rows.Select(ToTreeNode).ToList();
            }).ConfigureAwait(true);

            var truncated = children.Count > ChildDisplayLimit;
            if (truncated) children = children.Take(ChildDisplayLimit).ToList();

            Children.Clear();
            foreach (var child in children)
            {
                var childNode = new FileNodeViewModel(_host, child, this);
                // 目录需要预先知道是否有子项，才能决定是否显示展开箭头
                if (child.Kind != EntryKind.File) childNode.Children.Add(CreatePlaceholder());
                Children.Add(childNode);
            }

            if (truncated)
                Children.Add(CreatePlaceholder($"（已达显示上限 {ChildDisplayLimit:N0} 项，展开下一级查看其余）"));

            if (Children.Count == 0)
                Children.Add(CreatePlaceholder("（空）"));
        }
        catch (Exception ex)
        {
            Log.Warn($"展开节点失败 {Path}：{ex.Message}");
            Children.Clear();
            Children.Add(CreatePlaceholder("（读取失败）"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>强制重新加载（扫描/移动/删除后刷新）。</summary>
    public async Task RefreshAsync()
    {
        _childrenLoaded = false;
        Children.Clear();
        if (Node.Kind != EntryKind.File && Node.HasChildren) Children.Add(CreatePlaceholder("加载中…"));
        if (_isExpanded) await LoadChildrenAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 批量选择用：勾选状态。
    /// 目录树是主操作路径，用户要能一次勾多个文件夹再统一删除/搬运，
    /// 因此勾选状态放在节点自身而不是界面层，树刷新后仍可读回。
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    private bool _isChecked;

    /// <summary>
    /// 单个目录一次最多展示的子项数。
    /// 上万子项的目录即使开了虚拟化，构造上万个 ViewModel 也会让界面停顿数秒，
    /// 因此设上限并明确提示"展下一级查看其余"，而不是假装全部列出。
    /// </summary>
    private const int ChildDisplayLimit = 500;

    /// <summary>该节点是目录还是文件（界面据此决定是否显示勾选框与图标）。</summary>
    public bool IsFolder => Node.Kind != EntryKind.File;

    /// <summary>树上的显示名（占位节点用灰色提示文字）。</summary>
    public string DisplayName => IsPlaceholder ? Node.Name : Node.Name;

    private static FileTreeNode ToTreeNode(FileEntryModel entry) => new()
    {
        Path = entry.Path,
        Name = entry.Name,
        Kind = entry.Kind,
        Size = entry.Size,
        SubtreeSize = entry.SubtreeSize,
        Category = entry.Category,
        Risk = entry.Risk,
        Recognized = entry.Recognized,
        Mtime = entry.Mtime,
        HasChildren = entry.Kind != EntryKind.File,
        ChildCount = 0,
    };
}

/// <summary>未识别文件条目。</summary>
public sealed class UnknownFileViewModel : ObservableObject
{
    private bool _selected;

    public UnknownFileViewModel(FileEntryModel entry) => Entry = entry;

    public FileEntryModel Entry { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public long Size => Entry.Size;
    public string SizeText => Format.Bytes(Entry.Size);
    public string MagicHex => string.IsNullOrWhiteSpace(Entry.MagicHex) ? "（无文件头数据）" : Entry.MagicHex!;
    public string Folder => Entry.Parent;
    public long Mtime => Entry.Mtime;
    public RiskLevel Risk => Entry.Risk;
}

/// <summary>清理预演条目。</summary>
public sealed class CleanupItemViewModel : ObservableObject
{
    private bool _selected = true;

    public CleanupItemViewModel(CleanupCandidate candidate) => Candidate = candidate;

    public CleanupCandidate Candidate { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            SelectionChanged?.Invoke();
        }
    }

    public string Name => Candidate.Name;
    public string Path => Candidate.Path;
    public long Size => Candidate.Size;
    public string SizeText => Format.Bytes(Candidate.Size);
    public string Category => Candidate.Category;
    public string Rule => Candidate.Rule;
    public RiskLevel Risk => Candidate.Risk;

    public event Action? SelectionChanged;
}

/// <summary>重复文件条目。</summary>
public sealed class DuplicateItemViewModel : ObservableObject
{
    private bool _selected;

    public DuplicateItemViewModel(FileEntryModel entry, bool isKeeper, string hash, long groupSize)
    {
        Entry = entry;
        IsKeeper = isKeeper;
        Hash = hash;
        GroupSize = groupSize;
        _selected = !isKeeper;
    }

    public FileEntryModel Entry { get; }
    public bool IsKeeper { get; }
    public string Hash { get; }
    public long GroupSize { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string SizeText => Format.Bytes(Entry.Size);
    public long Mtime => Entry.Mtime;
    public string RoleText => IsKeeper ? "保留（最早修改）" : "可删除副本";
}