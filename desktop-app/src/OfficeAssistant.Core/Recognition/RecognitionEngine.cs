using System.Collections.Concurrent;
using System.Diagnostics;
using OfficeAssistant.Core.Data;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Recognition;

public sealed class RecognitionStats
{
    public long Candidates { get; set; }
    public long Processed { get; set; }
    public long Recognized { get; set; }
    public long Unrecognized { get; set; }
    public long SkippedTooLarge { get; set; }
    public double Seconds { get; set; }

    public double Coverage => Processed <= 0 ? 0 : (double)Recognized / Processed * 100.0;

    public string Summary =>
        Candidates == 0
            ? "没有需要识别的文件"
            : $"识别 {Processed:N0}/{Candidates:N0}，覆盖率 {Coverage:0.#}%，" +
              $"未识别 {Unrecognized:N0}，超大文件跳过 {SkippedTooLarge:N0}，耗时 {Format.Duration(Seconds)}";
}

/// <summary>
/// 内容识别批处理：从库中取出尚未识别的文件，做双通道类型判定与内容摘要，
/// 结果回写 files 表（category / mime / summary / recognized / magic）。
/// </summary>
public sealed class RecognitionEngine
{
    /// <summary>文本类文件最大读取量之上限（超过则只记元信息，避免扫描拖慢）。</summary>
    public long MaxTextFileBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>媒体文件最大处理量（元信息读取较慢，限制总量）。</summary>
    public long MaxMediaFileBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    public int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>单次识别批量的上限（可分批执行以保持界面响应）。</summary>
    public int BatchLimit { get; set; } = 25000;

    public long CountPending(Database db) => db.CountUnrecognized();

    public RecognitionStats RunBatch(Database db, CancellationToken token)
    {
        var stats = new RecognitionStats();
        var stopwatch = Stopwatch.StartNew();

        var candidates = LoadCandidates(db);
        stats.Candidates = candidates.Count;
        if (candidates.Count == 0)
        {
            stopwatch.Stop();
            stats.Seconds = stopwatch.Elapsed.TotalSeconds;
            return stats;
        }

        var updates = new ConcurrentQueue<FileEntry>();
        var recognized = 0L;
        var unrecognized = 0L;
        var skipped = 0L;
        var processed = 0L;

        try
        {
            Parallel.ForEach(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = token },
                entry =>
                {
                    if (token.IsCancellationRequested) return;

                    var tooLarge =
                        (IsTextCategory(entry.Ext) && entry.Size > MaxTextFileBytes) ||
                        (IsMediaCategory(entry.Ext) && entry.Size > MaxMediaFileBytes);

                    if (tooLarge)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var result = ContentExtractor.Extract(entry.Path, entry.Ext);
                    entry.Recognized = result.Recognized;
                    entry.Category = result.Category;
                    entry.Mime = result.Mime;
                    entry.Summary = result.Summary;
                    entry.MagicHex = result.MagicHex;
                    updates.Enqueue(entry);

                    if (result.Recognized) Interlocked.Increment(ref recognized);
                    else Interlocked.Increment(ref unrecognized);
                    Interlocked.Increment(ref processed);

                    // 分批落库，避免内存堆积
                    if (updates.Count >= 2000) Flush(db, updates);
                });
        }
        catch (OperationCanceledException)
        {
            // 用户中止，落库已完成部分
        }

        Flush(db, updates);

        stats.Processed = processed;
        stats.Recognized = recognized;
        stats.Unrecognized = unrecognized;
        stats.SkippedTooLarge = skipped;
        stopwatch.Stop();
        stats.Seconds = stopwatch.Elapsed.TotalSeconds;
        return stats;
    }

    private List<FileEntry> LoadCandidates(Database db)
    {
        var rows = db.Connection.Query($"""
            SELECT path, ext, size, kind FROM files
            WHERE kind = 0 AND recognized = 0 AND volume <> ''
            ORDER BY size ASC LIMIT {BatchLimit};
            """, s => new FileEntry
        {
            Path = s.GetString(0),
            Ext = s.GetString(1),
            Size = s.GetInt64(2),
            Kind = (EntryKind)s.GetInt64(3),
        });
        return rows;
    }

    private static void Flush(Database db, ConcurrentQueue<FileEntry> queue)
    {
        var batch = new List<FileEntry>(2048);
        while (queue.TryDequeue(out var entry))
        {
            batch.Add(entry);
            if (batch.Count < 2048) continue;
            WriteBatch(db, batch);
            batch.Clear();
        }
        if (batch.Count > 0) WriteBatch(db, batch);
    }

    private static void WriteBatch(Database db, List<FileEntry> batch)
    {
        try
        {
            using var tx = db.Connection.BeginTransaction();
            foreach (var entry in batch)
            {
                db.Connection.Execute("""
                    UPDATE files SET recognized = ?, category = ?, mime = ?, summary = ?, magic = ?
                    WHERE path = ?;
                    """, entry.Recognized, (int)entry.Category, entry.Mime, entry.Summary, entry.MagicHex, entry.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"识别结果写入失败：{ex.Message}");
        }
    }

    private static bool IsTextCategory(string ext) => ext is
        ".txt" or ".md" or ".log" or ".csv" or ".tsv" or ".json" or ".xml" or ".yaml" or ".yml" or
        ".ini" or ".cfg" or ".conf" or ".html" or ".htm" or ".css" or ".js" or ".ts" or ".py" or
        ".cs" or ".java" or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".rs" or ".php" or ".rb" or
        ".sql" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".srt" or ".lrc" or ".url" or ".reg";

    private static bool IsMediaCategory(string ext) => ext is
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".mpg" or ".mpeg" or
        ".ts" or ".rmvb" or ".3gp" or ".m4v" or ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or
        ".wma" or ".ogg" or ".opus" or ".ape";
}
