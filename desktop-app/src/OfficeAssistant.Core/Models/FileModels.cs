namespace OfficeAssistant.Core.Models;

/// <summary>
/// files 表的一行：扫描到的文件或目录条目。
/// 字段与《功能说明》三·数据存储 files 表一一对应。
/// </summary>
public sealed class FileEntry
{
    public long Id { get; set; }

    /// <summary>完整路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>父目录完整路径（按 parent 建索引以加速树状图展开）。</summary>
    public string Parent { get; set; } = string.Empty;

    /// <summary>文件名（含扩展名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>扩展名，小写，含点号，无扩展名为空串。</summary>
    public string Ext { get; set; } = string.Empty;

    public EntryKind Kind { get; set; }

    public long Size { get; set; }

    /// <summary>最后写入时间，Unix 秒。</summary>
    public long Mtime { get; set; }

    /// <summary>MD5，仅在去重分析等必要场景计算，默认空。</summary>
    public string? Md5 { get; set; }

    public RiskLevel Risk { get; set; }

    public bool Recognized { get; set; }

    public FileCategory Category { get; set; }

    /// <summary>类型判定得到的 MIME。</summary>
    public string? Mime { get; set; }

    /// <summary>内容摘要：文本前 N KB 的标题与摘录，或图片/音视频元信息。</summary>
    public string? Summary { get; set; }

    /// <summary>所属卷（盘符根，如 D:\）。</summary>
    public string Volume { get; set; } = string.Empty;

    /// <summary>目录子树累计大小；仅目录条目有意义。</summary>
    public long SubtreeSize { get; set; }

    /// <summary>文件头（magic bytes）十六进制，用于未识别文件的人工判断。</summary>
    public string? MagicHex { get; set; }
}

/// <summary>
/// 目录指纹快照，用于增量扫描比对。
/// 约定：<see cref="ChildCount"/> 为该目录**上一轮完整遍历**得到的子树文件数（供跳过后汇总），
/// <see cref="TotalSize"/> 为该目录直接子文件的字节合计（目录本身不计大小）。
/// </summary>
public sealed class DirFingerprint
{
    public string Path { get; set; } = string.Empty;
    public long Mtime { get; set; }
    public int ChildCount { get; set; }
    public long TotalSize { get; set; }
}

/// <summary>卷（盘符）信息。</summary>
public sealed class VolumeInfo
{
    public string Root { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool Scannable { get; set; }
    public bool Selected { get; set; } = true;
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes * 100.0;
}

/// <summary>扫描进度快照。</summary>
public sealed class ScanProgress
{
    public ScanMode Mode { get; set; }
    public bool Running { get; set; }
    public string CurrentRoot { get; set; } = string.Empty;
    public long FilesSeen { get; set; }
    public long DirsSeen { get; set; }
    public long BytesSeen { get; set; }
    public long SkippedSubtrees { get; set; }
    public long AccessDenied { get; set; }
    public long SkippedReparsePoints { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public double ElapsedSeconds => ((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalSeconds;
    public double FilesPerSecond => ElapsedSeconds <= 0.05 ? 0 : FilesSeen / ElapsedSeconds;
}

/// <summary>树状图节点（懒加载）。</summary>
public sealed class TreeNode
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntryKind Kind { get; set; }
    public long Size { get; set; }
    public long SubtreeSize { get; set; }
    public FileCategory Category { get; set; }
    public RiskLevel Risk { get; set; }
    public bool Recognized { get; set; }
    public long Mtime { get; set; }
    public bool HasChildren { get; set; }
    public int ChildCount { get; set; }
}

/// <summary>待清理条目（预览用）。</summary>
public sealed class CleanupCandidate
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public RiskLevel Risk { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public PlanAction Action { get; set; } = PlanAction.Delete;
    public bool Selected { get; set; } = true;
}

/// <summary>清理规则（cleanup_rules 表）。</summary>
public sealed class CleanupRule
{
    public long Id { get; set; }

    /// <summary>通配符模式（支持 * 与 ?），匹配完整路径，不区分大小写。</summary>
    public string Pattern { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;
}

/// <summary>操作审计条目（operation_log 表）。</summary>
public sealed class OperationLogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Operation { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>可选卷选择项（扫描范围配置）。</summary>
public sealed class VolumeSelection
{
    public string Root { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = string.Empty;
}
