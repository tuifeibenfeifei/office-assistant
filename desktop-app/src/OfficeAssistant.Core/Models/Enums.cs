using System.Text.Json.Serialization;

namespace OfficeAssistant.Core.Models;

/// <summary>风险等级（对应功能说明：安全 / 谨慎 / 危险 三级）。</summary>
public enum RiskLevel
{
    /// <summary>可安全删除：临时文件、缓存、回收站、旧安装包、浏览器缓存。</summary>
    Safe = 0,

    /// <summary>谨慎：用户文档目录下的大文件、重复文件。</summary>
    Caution = 1,

    /// <summary>危险：系统目录、Program Files、运行中进程占用文件。此类永不进入一键清理清单。</summary>
    Dangerous = 2,
}

/// <summary>文件内容大类（用于统计与展示；识别失败的归入 Unknown）。</summary>
public enum FileCategory
{
    Unknown = 0,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Text,
    Image,
    Audio,
    Video,
    Archive,
    Executable,
    Installer,
    Code,
    Font,
    Database,
    Shortcut,
    System,
    Other,
}

/// <summary>条目的文件系统类型。</summary>
public enum EntryKind
{
    File = 0,
    Directory = 1,
    Volume = 2,
}

/// <summary>扫描模式。</summary>
public enum ScanMode
{
    /// <summary>全量首扫。</summary>
    Full = 0,

    /// <summary>基于目录指纹的增量扫描。</summary>
    Incremental = 1,
}

/// <summary>清理计划中的单个条目的处置方式。</summary>
public enum PlanAction
{
    Delete = 0,
    Keep = 1,
    Move = 2,
}

/// <summary>作业状态（平台状态与本地标记合并后的展示状态）。</summary>
public enum HomeworkState
{
    /// <summary>平台无状态信息（本地模式/未同步）。</summary>
    Unsynced = 0,

    /// <summary>未提交。</summary>
    NotSubmitted = 1,

    /// <summary>已提交（平台确认）。</summary>
    Submitted = 2,

    /// <summary>已逾期（未提交且已过截止时间）。</summary>
    Overdue = 3,

    /// <summary>本地已标记完成。</summary>
    LocalDone = 4,
}

/// <summary>同步结果。</summary>
public enum SyncResult
{
    Success = 0,
    Failed = 1,
    Skipped = 2,

    /// <summary>会话失效，需要用户重新登录（不静默重试）。</summary>
    SessionExpired = 3,

    /// <summary>平台未提供开放接口 / 适配器未配置端点，降级为手动模式。</summary>
    Degraded = 4,
}
