namespace OfficeAssistant.Core.Models;

/// <summary>课程主数据（course 表）。</summary>
public sealed class Course
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Teacher { get; set; } = string.Empty;
    public string Term { get; set; } = string.Empty;
    public string? Source { get; set; }
    public DateTime SyncTime { get; set; } = DateTime.Now;
}

/// <summary>作业（homework 表）。</summary>
public sealed class Homework
{
    public string Id { get; set; } = string.Empty;
    public string CourseId { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>截止时间；null 表示平台未给出。</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>平台原始状态字符串（原样保留，便于比对）。</summary>
    public string? PlatformStatus { get; set; }

    /// <summary>平台归一化状态：not_submitted / submitted / overdue / unknown。</summary>
    public string PlatformState { get; set; } = "unknown";

    public DateTime? SubmittedAt { get; set; }

    /// <summary>本地完成标记。</summary>
    public bool LocalDone { get; set; }

    public string? Source { get; set; }
    public DateTime SyncTime { get; set; } = DateTime.Now;

    /// <summary>合并后的展示状态：平台状态与本地完成标记合并。</summary>
    public HomeworkState State
    {
        get
        {
            if (LocalDone) return HomeworkState.LocalDone;
            if (PlatformState == "submitted") return HomeworkState.Submitted;
            if (Deadline.HasValue && Deadline.Value < DateTime.Now && PlatformState != "submitted")
                return HomeworkState.Overdue;
            if (PlatformState == "not_submitted") return HomeworkState.NotSubmitted;
            if (PlatformState == "overdue") return HomeworkState.Overdue;
            return HomeworkState.Unsynced;
        }
    }

    /// <summary>距离截止的剩余时间描述。</summary>
    public string RemainingText
    {
        get
        {
            if (!Deadline.HasValue) return "未给出截止时间";
            var span = Deadline.Value - DateTime.Now;
            if (span.TotalSeconds < 0)
            {
                var over = Deadline.Value - DateTime.Now;
                over = DateTime.Now - Deadline.Value;
                if (over.TotalDays >= 1) return $"已逾期 {over.Days} 天";
                if (over.TotalHours >= 1) return $"已逾期 {(int)over.TotalHours} 小时";
                return $"已逾期 {(int)over.TotalMinutes} 分钟";
            }
            if (span.TotalDays >= 1) return $"剩余 {span.Days} 天 {span.Hours} 小时";
            if (span.TotalHours >= 1) return $"剩余 {(int)span.TotalHours} 小时 {span.Minutes} 分";
            return $"剩余 {(int)span.TotalMinutes} 分钟";
        }
    }

    public string StateText => State switch
    {
        HomeworkState.LocalDone => "本地已完成",
        HomeworkState.Submitted => "已提交",
        HomeworkState.Overdue => "已逾期",
        HomeworkState.NotSubmitted => "未提交",
        _ => "状态未知",
    };
}

/// <summary>同步日志（sync_log 表）。</summary>
public sealed class SyncLogEntry
{
    public long Id { get; set; }
    public DateTime SyncTime { get; set; } = DateTime.Now;
    public SyncResult Result { get; set; }
    public string? ErrorMessage { get; set; }
    public long DurationMs { get; set; }
    public int CourseCount { get; set; }
    public int HomeworkCount { get; set; }
    public string? Adapter { get; set; }
    public string ResultText => Result switch
    {
        SyncResult.Success => "成功",
        SyncResult.Failed => "失败",
        SyncResult.Skipped => "已跳过",
        SyncResult.SessionExpired => "会话失效，需重新登录",
        SyncResult.Degraded => "降级为本地模式",
        _ => Result.ToString(),
    };
}

/// <summary>登录凭据（经 DPAPI 加密后落盘，明文只在内存中存在）。</summary>
public sealed class Credential
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>扫码登录得到的会话令牌（可选）。</summary>
    public string? SessionToken { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string LoginMode { get; set; } = "password";
}
