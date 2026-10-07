using OfficeAssistant.Core.Models;

namespace OfficeAssistant.Core.Recognition;

/// <summary>类型规则：扩展名 → MIME + 大类 + magic 特征名。</summary>
public sealed record TypeRule(string Ext, string Mime, FileCategory Category, string MagicName);

/// <summary>
/// 扩展名映射表（双通道判定的第一通道）。
/// 覆盖常见办公文档、图片、音视频、压缩包、可执行体、代码、字体与数据库文件。
/// 表内容在首次运行时写入 SQLite 的 file_types 表，便于后续维护与审计。
/// </summary>
public static class FileTypeTable
{
    public static readonly IReadOnlyList<TypeRule> Rules =
    [
        // 文档
        new(".doc", "application/msword", FileCategory.Document, "OLE2"),
        new(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileCategory.Document, "ZIP"),
        new(".docm", "application/vnd.ms-word.document.macroEnabled.12", FileCategory.Document, "ZIP"),
        new(".dot", "application/msword", FileCategory.Document, "OLE2"),
        new(".dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template", FileCategory.Document, "ZIP"),
        new(".rtf", "application/rtf", FileCategory.Document, "RTF"),
        new(".odt", "application/vnd.oasis.opendocument.text", FileCategory.Document, "ZIP"),
        new(".wps", "application/vnd.ms-works", FileCategory.Document, "OLE2"),
        new(".pages", "application/x-iwork-pages-sffpages", FileCategory.Document, "ZIP"),

        // 表格
        new(".xls", "application/vnd.ms-excel", FileCategory.Spreadsheet, "OLE2"),
        new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileCategory.Spreadsheet, "ZIP"),
        new(".xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12", FileCategory.Spreadsheet, "ZIP"),
        new(".xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12", FileCategory.Spreadsheet, "ZIP"),
        new(".csv", "text/csv", FileCategory.Spreadsheet, "TEXT"),
        new(".tsv", "text/tab-separated-values", FileCategory.Spreadsheet, "TEXT"),
        new(".ods", "application/vnd.oasis.opendocument.spreadsheet", FileCategory.Spreadsheet, "ZIP"),
        new(".et", "application/vnd.ms-excel", FileCategory.Spreadsheet, "OLE2"),

        // 演示
        new(".ppt", "application/vnd.ms-powerpoint", FileCategory.Presentation, "OLE2"),
        new(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", FileCategory.Presentation, "ZIP"),
        new(".pptm", "application/vnd.ms-powerpoint.presentation.macroEnabled.12", FileCategory.Presentation, "ZIP"),
        new(".odp", "application/vnd.oasis.opendocument.presentation", FileCategory.Presentation, "ZIP"),
        new(".dps", "application/vnd.ms-powerpoint", FileCategory.Presentation, "OLE2"),

        // PDF
        new(".pdf", "application/pdf", FileCategory.Pdf, "PDF"),

        // 纯文本
        new(".txt", "text/plain", FileCategory.Text, "TEXT"),
        new(".md", "text/markdown", FileCategory.Text, "TEXT"),
        new(".log", "text/plain", FileCategory.Text, "TEXT"),
        new(".ini", "text/plain", FileCategory.Text, "TEXT"),
        new(".cfg", "text/plain", FileCategory.Text, "TEXT"),
        new(".conf", "text/plain", FileCategory.Text, "TEXT"),
        new(".json", "application/json", FileCategory.Text, "TEXT"),
        new(".xml", "application/xml", FileCategory.Text, "TEXT"),
        new(".yaml", "application/yaml", FileCategory.Text, "TEXT"),
        new(".yml", "application/yaml", FileCategory.Text, "TEXT"),
        new(".srt", "application/x-subrip", FileCategory.Text, "TEXT"),
        new(".lrc", "text/plain", FileCategory.Text, "TEXT"),

        // 图片
        new(".jpg", "image/jpeg", FileCategory.Image, "JPEG"),
        new(".jpeg", "image/jpeg", FileCategory.Image, "JPEG"),
        new(".jpe", "image/jpeg", FileCategory.Image, "JPEG"),
        new(".png", "image/png", FileCategory.Image, "PNG"),
        new(".gif", "image/gif", FileCategory.Image, "GIF"),
        new(".bmp", "image/bmp", FileCategory.Image, "BMP"),
        new(".webp", "image/webp", FileCategory.Image, "WEBP"),
        new(".tif", "image/tiff", FileCategory.Image, "TIFF"),
        new(".tiff", "image/tiff", FileCategory.Image, "TIFF"),
        new(".svg", "image/svg+xml", FileCategory.Image, "TEXT"),
        new(".ico", "image/x-icon", FileCategory.Image, "ICO"),
        new(".heic", "image/heic", FileCategory.Image, "ISO-BMFF"),
        new(".heif", "image/heif", FileCategory.Image, "ISO-BMFF"),
        new(".avif", "image/avif", FileCategory.Image, "ISO-BMFF"),
        new(".raw", "image/x-raw", FileCategory.Image, ""),
        new(".psd", "image/vnd.adobe.photoshop", FileCategory.Image, "PSD"),

        // 音频
        new(".mp3", "audio/mpeg", FileCategory.Audio, "ID3/MPEG"),
        new(".wav", "audio/wav", FileCategory.Audio, "RIFF"),
        new(".flac", "audio/flac", FileCategory.Audio, "FLAC"),
        new(".aac", "audio/aac", FileCategory.Audio, ""),
        new(".m4a", "audio/mp4", FileCategory.Audio, "ISO-BMFF"),
        new(".wma", "audio/x-ms-wma", FileCategory.Audio, "ASF"),
        new(".ogg", "audio/ogg", FileCategory.Audio, "OGG"),
        new(".opus", "audio/opus", FileCategory.Audio, "OGG"),
        new(".ape", "audio/x-ape", FileCategory.Audio, "APE"),
        new(".mid", "audio/midi", FileCategory.Audio, "MIDI"),

        // 视频
        new(".mp4", "video/mp4", FileCategory.Video, "ISO-BMFF"),
        new(".m4v", "video/x-m4v", FileCategory.Video, "ISO-BMFF"),
        new(".mkv", "video/x-matroska", FileCategory.Video, "MKV"),
        new(".avi", "video/x-msvideo", FileCategory.Video, "RIFF"),
        new(".mov", "video/quicktime", FileCategory.Video, "ISO-BMFF"),
        new(".wmv", "video/x-ms-wmv", FileCategory.Video, "ASF"),
        new(".flv", "video/x-flv", FileCategory.Video, "FLV"),
        new(".webm", "video/webm", FileCategory.Video, "MKV"),
        new(".mpg", "video/mpeg", FileCategory.Video, "MPEG-PS"),
        new(".mpeg", "video/mpeg", FileCategory.Video, "MPEG-PS"),
        new(".ts", "video/mp2t", FileCategory.Video, "MPEG-TS"),
        new(".rmvb", "application/vnd.rn-realmedia-vbr", FileCategory.Video, "RM"),
        new(".3gp", "video/3gpp", FileCategory.Video, "ISO-BMFF"),

        // 压缩包与镜像
        new(".zip", "application/zip", FileCategory.Archive, "ZIP"),
        new(".rar", "application/vnd.rar", FileCategory.Archive, "RAR"),
        new(".7z", "application/x-7z-compressed", FileCategory.Archive, "7Z"),
        new(".tar", "application/x-tar", FileCategory.Archive, "TAR"),
        new(".gz", "application/gzip", FileCategory.Archive, "GZIP"),
        new(".bz2", "application/x-bzip2", FileCategory.Archive, "BZIP2"),
        new(".xz", "application/x-xz", FileCategory.Archive, "XZ"),
        new(".iso", "application/x-iso9660-image", FileCategory.Archive, "ISO"),
        new(".cab", "application/vnd.ms-cab-compressed", FileCategory.Archive, "CAB"),
        new(".jar", "application/java-archive", FileCategory.Archive, "ZIP"),
        new(".apk", "application/vnd.android.package-archive", FileCategory.Archive, "ZIP"),
        new(".war", "application/java-archive", FileCategory.Archive, "ZIP"),

        // 可执行体与安装包
        new(".exe", "application/vnd.microsoft.portable-executable", FileCategory.Executable, "PE"),
        new(".msi", "application/x-msi", FileCategory.Installer, "OLE2"),
        new(".msix", "application/msix", FileCategory.Installer, "ZIP"),
        new(".appx", "application/appx", FileCategory.Installer, "ZIP"),
        new(".msu", "application/x-msu", FileCategory.Installer, "CAB"),
        new(".dll", "application/vnd.microsoft.portable-executable", FileCategory.System, "PE"),
        new(".sys", "application/octet-stream", FileCategory.System, "PE"),
        new(".scr", "application/vnd.microsoft.portable-executable", FileCategory.Executable, "PE"),
        new(".bat", "text/plain", FileCategory.Code, "TEXT"),
        new(".cmd", "text/plain", FileCategory.Code, "TEXT"),
        new(".ps1", "text/plain", FileCategory.Code, "TEXT"),
        new(".vbs", "text/plain", FileCategory.Code, "TEXT"),
        new(".reg", "text/plain", FileCategory.System, "TEXT"),
        new(".lnk", "application/x-ms-shortcut", FileCategory.Shortcut, "LNK"),
        new(".url", "text/plain", FileCategory.Shortcut, "TEXT"),

        // 代码
        new(".c", "text/x-c", FileCategory.Code, "TEXT"),
        new(".h", "text/x-c", FileCategory.Code, "TEXT"),
        new(".cpp", "text/x-c++", FileCategory.Code, "TEXT"),
        new(".hpp", "text/x-c++", FileCategory.Code, "TEXT"),
        new(".cs", "text/x-csharp", FileCategory.Code, "TEXT"),
        new(".java", "text/x-java", FileCategory.Code, "TEXT"),
        new(".py", "text/x-python", FileCategory.Code, "TEXT"),
        new(".js", "text/javascript", FileCategory.Code, "TEXT"),
        new(".ts", "text/typescript", FileCategory.Code, "TEXT"),
        new(".tsx", "text/typescript", FileCategory.Code, "TEXT"),
        new(".jsx", "text/javascript", FileCategory.Code, "TEXT"),
        new(".go", "text/x-go", FileCategory.Code, "TEXT"),
        new(".rs", "text/x-rust", FileCategory.Code, "TEXT"),
        new(".php", "text/x-php", FileCategory.Code, "TEXT"),
        new(".rb", "text/x-ruby", FileCategory.Code, "TEXT"),
        new(".sql", "application/sql", FileCategory.Code, "TEXT"),
        new(".html", "text/html", FileCategory.Code, "TEXT"),
        new(".htm", "text/html", FileCategory.Code, "TEXT"),
        new(".css", "text/css", FileCategory.Code, "TEXT"),
        new(".vue", "text/plain", FileCategory.Code, "TEXT"),
        new(".sh", "text/x-shellscript", FileCategory.Code, "TEXT"),

        // 字体
        new(".ttf", "font/ttf", FileCategory.Font, "TTF"),
        new(".otf", "font/otf", FileCategory.Font, "OTTO"),
        new(".ttc", "font/collection", FileCategory.Font, "TTC"),
        new(".woff", "font/woff", FileCategory.Font, "WOFF"),
        new(".woff2", "font/woff2", FileCategory.Font, "WOFF2"),
        new(".fon", "application/x-font", FileCategory.Font, ""),

        // 数据库与数据
        new(".db", "application/vnd.sqlite3", FileCategory.Database, "SQLITE"),
        new(".sqlite", "application/vnd.sqlite3", FileCategory.Database, "SQLITE"),
        new(".sqlite3", "application/vnd.sqlite3", FileCategory.Database, "SQLITE"),
        new(".mdb", "application/x-msaccess", FileCategory.Database, "OLE2"),
        new(".accdb", "application/x-msaccess", FileCategory.Database, "OLE2"),
        new(".pst", "application/vnd.ms-outlook", FileCategory.Database, "OLE2"),
        new(".dat", "application/octet-stream", FileCategory.Other, ""),
        new(".bin", "application/octet-stream", FileCategory.Other, ""),
        new(".pak", "application/octet-stream", FileCategory.Other, ""),
        new(".cache", "application/octet-stream", FileCategory.Other, ""),
        new(".tmp", "application/octet-stream", FileCategory.Other, ""),
        new(".etl", "application/octet-stream", FileCategory.System, ""),
        new(".evtx", "application/octet-stream", FileCategory.System, ""),
        new(".pf", "application/octet-stream", FileCategory.System, ""),
        new(".thumbnails", "application/octet-stream", FileCategory.Image, ""),
        new(".db-journal", "application/octet-stream", FileCategory.Database, ""),

        // 其他常见
        new(".torrent", "application/x-bittorrent", FileCategory.Other, "BENCODE"),
        new(".epub", "application/epub+zip", FileCategory.Document, "ZIP"),
        new(".mobi", "application/x-mobipocket-ebook", FileCategory.Document, "BOOKMOBI"),
        new(".azw3", "application/vnd.amazon.ebook", FileCategory.Document, "BOOKMOBI"),
        new(".dwg", "image/vnd.dwg", FileCategory.Other, "DWG"),
        new(".dxf", "image/vnd.dxf", FileCategory.Other, "TEXT"),
        new(".stl", "model/stl", FileCategory.Other, ""),
        new(".obj", "model/obj", FileCategory.Other, "TEXT"),
        new(".eot", "application/vnd.ms-fontobject", FileCategory.Font, ""),
        new(".iso9660", "application/x-iso9660-image", FileCategory.Archive, "ISO"),
    ];

    private static readonly Dictionary<string, TypeRule> ByExt =
        Rules.GroupBy(r => r.Ext).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static TypeRule? Lookup(string ext)
        => !string.IsNullOrEmpty(ext) && ByExt.TryGetValue(ext, out var rule) ? rule : null;
}
