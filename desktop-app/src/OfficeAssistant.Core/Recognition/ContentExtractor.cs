using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using OfficeAssistant.Core.Models;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.Core.Recognition;

/// <summary>单个文件的内容识别结果。</summary>
public sealed record ContentResult(
    bool Recognized,
    FileCategory Category,
    string? Mime,
    string? Summary,
    string? MagicHex,
    string? ThumbnailPath = null,
    long DurationSeconds = 0)
{
    public static ContentResult Unknown(string? magicHex, FileCategory category = FileCategory.Unknown)
        => new(false, category, null, null, magicHex);
}

/// <summary>
/// 内容识别引擎（功能三）。
///
/// 双通道判定：扩展名映射为主、文件头 magic bytes 校验为辅，可发现伪装扩展名；
/// 内容提取：文本读前 N KB 生成标题与摘要、图片生成不超过 128 像素的缩略图、
///          音视频读取时长与分辨率等元信息；Office 文档直接解析 OOXML 包内 XML 取得标题。
/// 识别失败的统一进入"未识别文件区"，并保留文件头十六进制供用户人工判断。
/// </summary>
public static class ContentExtractor
{
    public const int MaxTextBytes = 64 * 1024;
    public const int MaxThumbnailPixels = 128;
    public const long MaxThumbnailSourceBytes = 64L * 1024 * 1024;

    public static ContentResult Extract(string path, string ext)
    {
        try
        {
            if (!File.Exists(path)) return ContentResult.Unknown(null);

            var header = ReadHeader(path, MagicDetector.SniffLength);
            var magic = MagicDetector.Detect(header);
            var hex = header.Length == 0 ? null : MagicDetector.ToHex(header);
            var rule = FileTypeTable.Lookup(ext);

            // 判定优先级：
            //  1) 扩展名与文件头冲突（伪装扩展名）→ 报实际类型并提示不一致；
            //  2) 扩展名可用且与文件头兼容 → 用扩展名（容器格式的文件头往往过于笼统，
            //     例如 docx/xlsx/pptx/jar/apk 的文件头都是 ZIP，仅凭文件头无法区分）；
            //  3) 仅有文件头 → 用文件头；
            //  4) 都没有 → 未识别区。
            var compatible = rule is not null && magic is { } m && IsCompatible(rule, m);

            if (rule is not null && magic is { } magicValue && !compatible)
            {
                return new ContentResult(true, magicValue.Category, magicValue.Mime ?? rule.Mime,
                    $"⚠ 文件头为 {magicValue.Name}，与扩展名 {rule.Ext}（{DescribeCategory(rule.Category)}）不一致，可能被改名；" +
                    $"实际类型：{DescribeCategory(magicValue.Category)}",
                    hex);
            }

            if (rule is not null)
            {
                var magicName = magic?.Name ?? rule.MagicName;
                return ExtractByCategory(path, ext, rule.Category, rule.Mime, hex, magicName, header);
            }

            if (magic is { } onlyMagicValue)
                return ExtractByCategory(path, ext, onlyMagicValue.Category, onlyMagicValue.Mime, hex, onlyMagicValue.Name, header);

            // 双通道都未命中 → 未识别区
            return ContentResult.Unknown(hex);
        }
        catch (Exception)
        {
            return ContentResult.Unknown(null);
        }
    }

    /// <summary>
    /// 判断"扩展名声明的类型"与"文件头探测到的类型"是否兼容。
    /// 只把明显冲突（例如 .jpg 里装着 PE 可执行体、.txt 里装着 ZIP）判为不一致，
    /// 以免把 OOXML 这类基于 ZIP 容器的合法文件误报为伪装。
    /// </summary>
    private static bool IsCompatible(TypeRule rule, MagicResult magic)
    {
        // 同类别直接兼容
        if (rule.Category == magic.Category) return true;

        // 容器格式：ZIP / OLE2 作为载荷容器被多种办公与归档格式复用
        var containerFormats = new[] { "ZIP", "OLE2", "ISO-BMFF", "RIFF", "MKV" };
        if (containerFormats.Contains(magic.Name, StringComparer.OrdinalIgnoreCase))
        {
            return rule.Category is FileCategory.Document or FileCategory.Spreadsheet
                or FileCategory.Presentation or FileCategory.Archive or FileCategory.Image
                or FileCategory.Audio or FileCategory.Video or FileCategory.Font
                or FileCategory.Database or FileCategory.Executable or FileCategory.Installer;
        }

        // 文本启发式：扩展名声明文本/代码/表格/文档，或文件头只是判定为文本
        if (magic.Name == "TEXT")
        {
            return rule.Category is FileCategory.Text or FileCategory.Code
                or FileCategory.Spreadsheet or FileCategory.Document or FileCategory.Other;
        }

        // 字体、快捷方式等专用容器
        if (magic.Category == FileCategory.Font && rule.Category == FileCategory.Font) return true;

        // 规则里已经声明了相同 magic 特征名
        if (rule.MagicName.Equals(magic.Name, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static ContentResult ExtractByCategory(string path, string ext, FileCategory category, string? mime,
        string? hex, string magicName, byte[] header)
    {
        switch (category)
        {
            case FileCategory.Image:
            {
                var info = ImageHeader.Parse(path, header);
                var thumb = TryCreateThumbnail(path);
                var parts = new List<string>();
                if (info.Width > 0) parts.Add($"{info.Width} × {info.Height} 像素");
                if (info.Frames > 1) parts.Add($"{info.Frames} 帧");
                parts.Add(Format.Bytes(new FileInfo(path).Length));
                if (info.Detail is not null) parts.Add(info.Detail);
                return new ContentResult(true, FileCategory.Image, mime, string.Join("，", parts), hex, thumb);
            }

            case FileCategory.Video:
            case FileCategory.Audio:
            {
                var text = DescribeMedia(path, category);
                return new ContentResult(true, category, mime, text, hex, null, 0);
            }

            case FileCategory.Text:
            case FileCategory.Code:
            case FileCategory.Spreadsheet when ext is ".csv" or ".tsv":
            {
                var text = TextSummary(path);
                var detected = category == FileCategory.Spreadsheet ? FileCategory.Spreadsheet : category;
                return new ContentResult(true, detected, mime, text, hex);
            }

            case FileCategory.Document:
            {
                var text = ext switch
                {
                    ".docx" or ".docm" or ".dotx" or ".odt" or ".epub" => OoxmlSummary(path, ext),
                    ".pdf" => PdfSummary(path),
                    ".rtf" => TextSummary(path),
                    _ => null,
                };
                return new ContentResult(true, category, mime, text, hex);
            }

            case FileCategory.Presentation:
            {
                var text = ext is ".pptx" or ".pptm" or ".odp" ? OoxmlSummary(path, ext) : null;
                return new ContentResult(true, category, mime, text, hex);
            }

            case FileCategory.Spreadsheet:
            {
                var text = ext is ".xlsx" or ".xlsm" or ".xlsb" or ".ods" ? OoxmlSummary(path, ext) : null;
                return new ContentResult(true, category, mime, text, hex);
            }

            case FileCategory.Pdf:
                return new ContentResult(true, FileCategory.Pdf, mime, PdfSummary(path), hex);

            case FileCategory.Archive:
            {
                var text = ext is ".zip" or ".jar" or ".apk" or ".msix" or ".appx" or ".war"
                    ? ZipSummary(path)
                    : null;
                return new ContentResult(true, category, mime, text, hex);
            }

            case FileCategory.Font:
                return new ContentResult(true, category, mime, $"字体文件（{magicName}），{Format.Bytes(new FileInfo(path).Length)}", hex);

            case FileCategory.Executable:
            case FileCategory.Installer:
            {
                var version = ShellPropertyReader.GetProperty(path, "产品名称", "Product name", "文件说明", "File description");
                var company = ShellPropertyReader.GetProperty(path, "公司", "Company", "版权", "Copyright");
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(version)) parts.Add(version!);
                if (!string.IsNullOrWhiteSpace(company)) parts.Add(company!);
                parts.Add($"可执行文件，{Format.Bytes(new FileInfo(path).Length)}");
                return new ContentResult(true, category, mime, string.Join(" · ", parts), hex);
            }

            case FileCategory.Shortcut:
            {
                var target = ShellPropertyReader.GetProperty(path, "目标", "Target");
                return new ContentResult(true, category, mime,
                    string.IsNullOrWhiteSpace(target) ? "快捷方式" : $"快捷方式 → {target}", hex);
            }

            case FileCategory.Database:
                return new ContentResult(true, category, mime,
                    $"数据库文件（{magicName}），{Format.Bytes(new FileInfo(path).Length)}", hex);

            case FileCategory.System:
            case FileCategory.Other:
            default:
            {
                // 有的"其他"类其实是文本（如 DXF/OBJ），再做一次启发式
                if (MagicDetector.LooksLikeText(header))
                {
                    return new ContentResult(true, FileCategory.Text, "text/plain", TextSummary(path), hex);
                }
                return new ContentResult(true, category, mime,
                    $"{DescribeCategory(category)}（{magicName}），{Format.Bytes(new FileInfo(path).Length)}", hex);
            }
        }
    }

    public static string DescribeCategory(FileCategory category) => category switch
    {
        FileCategory.Document => "文档",
        FileCategory.Spreadsheet => "表格",
        FileCategory.Presentation => "演示文稿",
        FileCategory.Pdf => "PDF",
        FileCategory.Text => "文本",
        FileCategory.Image => "图片",
        FileCategory.Audio => "音频",
        FileCategory.Video => "视频",
        FileCategory.Archive => "压缩包/镜像",
        FileCategory.Executable => "可执行程序",
        FileCategory.Installer => "安装包",
        FileCategory.Code => "代码/脚本",
        FileCategory.Font => "字体",
        FileCategory.Database => "数据库",
        FileCategory.Shortcut => "快捷方式",
        FileCategory.System => "系统文件",
        FileCategory.Other => "其他",
        _ => "未知",
    };

    // ------------------------------------------------------------------ 文本

    /// <summary>读取纯文本前 N KB，生成标题与摘要。</summary>
    public static string TextSummary(string path)
    {
        try
        {
            var bytes = ReadPrefix(path, MaxTextBytes);
            if (bytes.Length == 0) return "空文件";

            var (text, encodingName) = Decode(bytes);
            var trimmed = text.TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
            if (trimmed.Length == 0) return "空文件";

            var lines = trimmed.Split('\n');
            var title = lines[0].Trim();
            if (title.Length > 80) title = title[..80] + "…";

            var preview = new StringBuilder();
            foreach (var line in lines.Skip(0))
            {
                var t = line.TrimEnd('\r').Trim();
                if (t.Length == 0) continue;
                if (preview.Length + t.Length > 280) break;
                if (preview.Length > 0) preview.Append(" / ");
                preview.Append(t);
            }

            var result = $"标题：{title}｜编码 {encodingName}｜{lines.Length} 行（前 {Format.Bytes(bytes.Length)}）";
            if (preview.Length > 0) result += "\n摘要：" + preview;
            return result;
        }
        catch (Exception ex)
        {
            return $"（读取文本失败：{ex.Message}）";
        }
    }

    private static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // 中文 Windows 常见的 GBK/GB18030 内容
            try
            {
                var gbk = Encoding.GetEncoding(936);
                return (gbk.GetString(bytes), "GBK");
            }
            catch
            {
                return (Encoding.UTF8.GetString(bytes), "UTF-8（宽松）");
            }
        }
    }

    // ------------------------------------------------------------------ 图片

    public static string? TryCreateThumbnail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is 0 or > MaxThumbnailSourceBytes) return null;

            var target = Path.Combine(AppPaths.CacheDirectory, ThumbnailName(path));
            if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

            using var source = Image.FromFile(path);
            var ratio = Math.Min(
                (double)MaxThumbnailPixels / Math.Max(1, source.Width),
                (double)MaxThumbnailPixels / Math.Max(1, source.Height));
            var w = Math.Max(1, (int)(source.Width * ratio));
            var h = Math.Max(1, (int)(source.Height * ratio));

            using var thumb = new Bitmap(w, h);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }
            thumb.Save(target, ImageFormat.Png);
            TrimCache();
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static string ThumbnailName(string path)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(PathToolsKey(path)));
        return Convert.ToHexString(bytes).ToLowerInvariant() + ".png";
    }

    private static string PathToolsKey(string path) => path.ToUpperInvariant();

    private static void TrimCache()
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.CacheDirectory).GetFiles("*.png");
            const int maxFiles = 4000;
            if (files.Length <= maxFiles) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - maxFiles))
            {
                try { f.Delete(); } catch { /* 忽略 */ }
            }
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 媒体

    private static string DescribeMedia(string path, FileCategory category)
    {
        var size = Format.Bytes(new FileInfo(path).Length);
        var durationText = ShellPropertyReader.GetProperty(path, "时长", "长度", "Duration", "Length");
        var duration = ShellPropertyReader.ParseDuration(durationText);
        var resolutionText = ShellPropertyReader.GetProperty(path, "帧宽度", "Frame width", "视频宽度");
        var heightText = ShellPropertyReader.GetProperty(path, "帧高度", "Frame height", "视频高度");
        var resolution = ShellPropertyReader.ParseResolution(
            resolutionText is not null && heightText is not null ? $"{resolutionText} x {heightText}" : resolutionText);

        var parts = new List<string>();
        if (duration.HasValue)
        {
            parts.Add(duration.Value.TotalHours >= 1
                ? $"时长 {(int)duration.Value.TotalHours}:{duration.Value.Minutes:00}:{duration.Value.Seconds:00}"
                : $"时长 {duration.Value.Minutes:00}:{duration.Value.Seconds:00}");
        }

        if (category == FileCategory.Video && resolution.HasValue)
            parts.Add($"{resolution.Value.Width} × {resolution.Value.Height}");

        var title = ShellPropertyReader.GetProperty(path, "标题", "Title");
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"标题 {title}");

        var bitrate = ShellPropertyReader.GetProperty(path, "总比特率", "Total bitrate", "比特率", "Bit rate");
        if (!string.IsNullOrWhiteSpace(bitrate)) parts.Add($"码率 {bitrate}");

        parts.Add(size);
        if (!duration.HasValue && category == FileCategory.Video)
            parts.Add("系统未提供时长信息");

        return string.Join("，", parts);
    }

    // ------------------------------------------------------------------ Office / PDF / 压缩包

    private static string? OoxmlSummary(string path, string ext)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var parts = new List<string>();

            // 核心属性：标题 / 作者
            var core = archive.GetEntry("docProps/core.xml");
            string? title = null;
            string? creator = null;
            if (core is not null)
            {
                var xml = ReadEntryText(core, 64 * 1024);
                if (xml is not null)
                {
                    var doc = SafeXml(xml);
                    title = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
                    creator = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(title)) parts.Add($"标题 {title!.Trim()}");
            if (!string.IsNullOrWhiteSpace(creator)) parts.Add($"作者 {creator!.Trim()}");

            var document = archive.GetEntry("word/document.xml");
            if (document is not null)
            {
                var xml = ReadEntryText(document, 256 * 1024);
                if (xml is not null)
                {
                    var doc = SafeXml(xml);
                    var paragraphs = doc?.Descendants().Where(e => e.Name.LocalName == "p").ToList();
                    var text = string.Join(" ", (paragraphs ?? [])
                        .Take(6)
                        .Select(p => string.Concat(p.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (text.Length > 200) text = text[..200] + "…";
                    if (text.Length > 0) parts.Add($"正文开头：{text}");
                    if (paragraphs is not null) parts.Add($"段落数约 {paragraphs.Count}{(paragraphs.Count >= 200 ? "+" : string.Empty)}");
                }
            }

            var workbook = archive.GetEntry("xl/workbook.xml");
            if (workbook is not null)
            {
                var xml = ReadEntryText(workbook, 128 * 1024);
                if (xml is not null)
                {
                    var doc = SafeXml(xml);
                    var sheets = doc?.Descendants().Where(e => e.Name.LocalName == "sheet")
                        .Select(e => e.Attribute("name")?.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();
                    if (sheets is { Count: > 0 })
                        parts.Add($"工作表 {sheets.Count} 个：{string.Join("、", sheets.Take(8))}");
                }
            }

            var presentation = archive.GetEntry("ppt/presentation.xml");
            if (presentation is not null)
            {
                var slides = archive.Entries.Count(e =>
                    e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                parts.Add($"幻灯片 {slides} 页");
            }

            if (parts.Count == 0)
                parts.Add($"{DescribeCategory(GuessCategory(ext))}（{Format.Bytes(new FileInfo(path).Length)}）");

            return string.Join("｜", parts);
        }
        catch (InvalidDataException)
        {
            return "（压缩包结构损坏，无法读取内容）";
        }
        catch (Exception ex)
        {
            return $"（解析失败：{ex.Message}）";
        }
    }

    private static FileCategory GuessCategory(string ext) => ext switch
    {
        ".docx" or ".docm" or ".dotx" or ".odt" or ".epub" or ".mobi" or ".azw3" => FileCategory.Document,
        ".xlsx" or ".xlsm" or ".xlsb" or ".ods" => FileCategory.Spreadsheet,
        ".pptx" or ".pptm" or ".odp" => FileCategory.Presentation,
        _ => FileCategory.Other,
    };

    private static string? PdfSummary(string path)
    {
        try
        {
            var text = ReadTextish(path, 512 * 1024);
            if (text is null) return "PDF 文档（未压缩文本流不可读）";

            var title = ExtractPdfField(text, "/Title");
            var author = ExtractPdfField(text, "/Author");
            var producer = ExtractPdfField(text, "/Producer");
            var pages = CountOccurrences(text, "/Type /Page") + CountOccurrences(text, "/Type/Page");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(title)) parts.Add($"标题 {title}");
            if (!string.IsNullOrWhiteSpace(author)) parts.Add($"作者 {author}");
            if (pages > 0) parts.Add($"页数约 {pages}");
            if (!string.IsNullOrWhiteSpace(producer)) parts.Add($"生成器 {producer}");
            parts.Add(Format.Bytes(new FileInfo(path).Length));
            return string.Join("｜", parts);
        }
        catch (Exception ex)
        {
            return $"（PDF 解析失败：{ex.Message}）";
        }
    }

    private static string? ExtractPdfField(string text, string field)
    {
        var idx = text.IndexOf(field, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = text.IndexOf('(', idx);
        if (start < 0) return null;
        var end = text.IndexOf(')', start + 1);
        if (end < 0 || end - start > 512) return null;
        var value = text[(start + 1)..end].Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? ZipSummary(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Count;
            var files = archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
            var top = archive.Entries
                .Select(e => e.FullName.Split('/')[0])
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var uncompressed = archive.Entries.Sum(e => e.Length);
            return $"含 {entries} 个条目（{files} 个文件），解压后约 {Format.Bytes(uncompressed)}" +
                   (top.Count > 0 ? $"｜顶层：{string.Join("、", top)}" : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static XDocument? SafeXml(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch { return null; }
    }

    private static string? ReadEntryText(ZipArchiveEntry entry, int maxBytes)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, false);
            var buffer = new char[maxBytes / 2];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return read <= 0 ? null : new string(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTextish(string path, int maxBytes)
    {
        try
        {
            var bytes = ReadPrefix(path, maxBytes);
            // PDF 常用 latin-1 存放元信息，这里直接用 latin-1 解码以避免替换字符
            return Encoding.Latin1.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ------------------------------------------------------------------ IO 辅助

    public static byte[] ReadHeader(string path, int length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(length, (int)Math.Min(length, stream.Length))];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch
        {
            return [];
        }
    }

    private static byte[] ReadPrefix(string path, int length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(length, (int)Math.Min(length, stream.Length))];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>图片尺寸直读（JPEG/PNG/GIF/BMP/WebP/TIFF），不依赖 System.Drawing，避免解码整图。</summary>
public static class ImageHeader
{
    public readonly record struct Info(int Width, int Height, int Frames, string? Detail);

    public static Info Parse(string path, ReadOnlySpan<byte> header)
    {
        try
        {
            if (header.Length >= 24 && header[0] == 0x89 && header[1] == 0x50)
            {
                var w = ReadInt32BE(header, 16);
                var h = ReadInt32BE(header, 20);
                return new Info(w, h, 1, "PNG");
            }

            if (header.Length >= 10 && (header[0] == (byte)'G') && header[1] == (byte)'I' && header[2] == (byte)'F')
            {
                var w = header[6] | (header[7] << 8);
                var h = header[8] | (header[9] << 8);
                var frames = 1;
                if (header.Length > 10 && header[10] != 0) frames = 0; // 动画 GIF，帧数不确定
                return new Info(w, h, frames, "GIF");
            }

            if (header.Length >= 26 && header[0] == (byte)'B' && header[1] == (byte)'M')
            {
                var w = BitConverter.ToInt32(header[18..22]);
                var h = BitConverter.ToInt32(header[22..26]);
                return new Info(Math.Abs(w), Math.Abs(h), 1, "BMP");
            }

            if (header.Length >= 30 && header[0] == 0xFF && header[1] == 0xD8)
                return ParseJpeg(path);

            if (header.Length >= 30 && header[0] == (byte)'R' && header[1] == (byte)'I' &&
                header[8] == (byte)'W' && header[9] == (byte)'E')
            {
                var w = BitConverter.ToInt32(header[26..30]) & 0x3FFF;
                var h = BitConverter.ToInt32(header[28..32]) & 0x3FFF;
                return new Info(w + 1, h + 1, 1, "WebP");
            }

            if (header.Length >= 4 && header[0] == 0x49 && header[1] == 0x49)
            {
                var w = BitConverter.ToUInt16(header[18..20]);
                var h = BitConverter.ToUInt16(header[20..22]);
                return new Info(w, h, 1, "TIFF");
            }
        }
        catch
        {
            // 忽略，交由降级路径
        }
        return new Info(0, 0, 1, null);
    }

    /// <summary>扫描 JPEG 标记段，找到 SOF0/SOF2 中的宽高。</summary>
    private static Info ParseJpeg(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            var read = stream.Read(buffer, 0, buffer.Length);
            var i = 2;
            while (i + 9 < read)
            {
                if (buffer[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                var marker = buffer[i + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB)
                {
                    var h = (buffer[i + 5] << 8) | buffer[i + 6];
                    var w = (buffer[i + 7] << 8) | buffer[i + 8];
                    return new Info(w, h, 1, "JPEG");
                }
                var length = (buffer[i + 2] << 8) | buffer[i + 3];
                if (length <= 0) break;
                i += 2 + length;
            }
        }
        catch
        {
            // 忽略
        }
        return new Info(0, 0, 1, "JPEG");
    }

    private static int ReadInt32BE(ReadOnlySpan<byte> data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
