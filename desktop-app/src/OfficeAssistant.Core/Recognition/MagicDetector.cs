using OfficeAssistant.Core.Models;

namespace OfficeAssistant.Core.Recognition;

/// <summary>magic bytes 判定结果。</summary>
public readonly record struct MagicResult(string Name, FileCategory Category, string? Mime, int HeaderLength);

/// <summary>
/// 文件头（magic bytes）判定——双通道判定的第二通道，
/// 用于发现"伪装扩展名"（例如把 .exe 改名为 .jpg）以及无扩展名文件。
/// </summary>
public static class MagicDetector
{
    public const int SniffLength = 512;

    /// <summary>读取文件头并判定类型。<paramref name="header"/> 为已读入的字节。</summary>
    public static MagicResult? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2) return null;

        // --- 图片
        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
            return new MagicResult("JPEG", FileCategory.Image, "image/jpeg", 3);
        if (StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return new MagicResult("PNG", FileCategory.Image, "image/png", 8);
        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
            return new MagicResult("GIF", FileCategory.Image, "image/gif", 6);
        if (StartsWith(header, "BM"u8))
            return new MagicResult("BMP", FileCategory.Image, "image/bmp", 2);
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return new MagicResult("WEBP", FileCategory.Image, "image/webp", 12);
        if (StartsWith(header, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(header, [0x4D, 0x4D, 0x00, 0x2A]))
            return new MagicResult("TIFF", FileCategory.Image, "image/tiff", 4);
        if (StartsWith(header, [0x00, 0x00, 0x01, 0x00]))
            return new MagicResult("ICO", FileCategory.Image, "image/x-icon", 4);
        if (StartsWith(header, "8BPS"u8))
            return new MagicResult("PSD", FileCategory.Image, "image/vnd.adobe.photoshop", 4);

        // --- 文档
        if (StartsWith(header, "%PDF-"u8))
            return new MagicResult("PDF", FileCategory.Pdf, "application/pdf", 5);
        if (StartsWith(header, "{\\rtf"u8))
            return new MagicResult("RTF", FileCategory.Document, "application/rtf", 5);
        if (StartsWith(header, "BOOKMOBI"u8))
            return new MagicResult("BOOKMOBI", FileCategory.Document, "application/x-mobipocket-ebook", 8);

        // --- 压缩/容器
        if (StartsWith(header, [0x50, 0x4B, 0x03, 0x04]) || StartsWith(header, [0x50, 0x4B, 0x05, 0x06]) ||
            StartsWith(header, [0x50, 0x4B, 0x07, 0x08]))
            return new MagicResult("ZIP", FileCategory.Archive, "application/zip", 4);
        if (StartsWith(header, "Rar!\u001A\u0007"u8))
            return new MagicResult("RAR", FileCategory.Archive, "application/vnd.rar", 7);
        if (StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            return new MagicResult("7Z", FileCategory.Archive, "application/x-7z-compressed", 6);
        if (StartsWith(header, [0x1F, 0x8B]))
            return new MagicResult("GZIP", FileCategory.Archive, "application/gzip", 2);
        if (StartsWith(header, "BZh"u8))
            return new MagicResult("BZIP2", FileCategory.Archive, "application/x-bzip2", 3);
        if (StartsWith(header, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
            return new MagicResult("XZ", FileCategory.Archive, "application/x-xz", 6);
        if (header.Length >= 8 && StartsWith(header, "MSCF"u8))
            return new MagicResult("CAB", FileCategory.Archive, "application/vnd.ms-cab-compressed", 4);
        if (header.Length >= 0x8005 && header[0x8001..0x8006].SequenceEqual("CD001"u8))
            return new MagicResult("ISO", FileCategory.Archive, "application/x-iso9660-image", 0x8006);

        // --- 多媒体容器
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
            return new MagicResult("ISO-BMFF", FileCategory.Video, "video/mp4", 12);
        if (StartsWith(header, [0x1A, 0x45, 0xDF, 0xA3]))
            return new MagicResult("MKV", FileCategory.Video, "video/x-matroska", 4);
        if (StartsWith(header, "FLV"u8))
            return new MagicResult("FLV", FileCategory.Video, "video/x-flv", 3);
        if (StartsWith(header, [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11]))
            return new MagicResult("ASF", FileCategory.Video, "video/x-ms-asf", 8);
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && header[8..12].SequenceEqual("AVI "u8))
            return new MagicResult("RIFF", FileCategory.Video, "video/x-msvideo", 12);
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
            return new MagicResult("RIFF", FileCategory.Audio, "audio/wav", 12);
        if (StartsWith(header, "fLaC"u8))
            return new MagicResult("FLAC", FileCategory.Audio, "audio/flac", 4);
        if (StartsWith(header, "OggS"u8))
            return new MagicResult("OGG", FileCategory.Audio, "audio/ogg", 4);
        if (StartsWith(header, "ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
            return new MagicResult("ID3/MPEG", FileCategory.Audio, "audio/mpeg", 3);
        if (StartsWith(header, "MAC "u8))
            return new MagicResult("APE", FileCategory.Audio, "audio/x-ape", 4);
        if (StartsWith(header, "MThd"u8))
            return new MagicResult("MIDI", FileCategory.Audio, "audio/midi", 4);
        if (StartsWith(header, "MPEG"u8) || StartsWith(header, [0x00, 0x00, 0x01, 0xBA]))
            return new MagicResult("MPEG-PS", FileCategory.Video, "video/mpeg", 4);
        if (StartsWith(header, ".RMF"u8))
            return new MagicResult("RM", FileCategory.Video, "application/vnd.rn-realmedia", 4);

        // --- 可执行体
        if (StartsWith(header, "MZ"u8))
            return new MagicResult("PE", FileCategory.Executable, "application/vnd.microsoft.portable-executable", 2);

        // --- 字体
        if (StartsWith(header, [0x00, 0x01, 0x00, 0x00]) || StartsWith(header, "true"u8))
            return new MagicResult("TTF", FileCategory.Font, "font/ttf", 4);
        if (StartsWith(header, "OTTO"u8))
            return new MagicResult("OTTO", FileCategory.Font, "font/otf", 4);
        if (StartsWith(header, "ttcf"u8))
            return new MagicResult("TTC", FileCategory.Font, "font/collection", 4);
        if (StartsWith(header, "wOFF"u8))
            return new MagicResult("WOFF", FileCategory.Font, "font/woff", 4);
        if (StartsWith(header, "wOF2"u8))
            return new MagicResult("WOFF2", FileCategory.Font, "font/woff2", 4);

        // --- 数据库 / 系统
        if (StartsWith(header, "SQLite format 3\u0000"u8))
            return new MagicResult("SQLITE", FileCategory.Database, "application/vnd.sqlite3", 16);
        if (StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            return new MagicResult("OLE2", FileCategory.Document, "application/x-ole-storage", 8);
        if (StartsWith(header, "ElfFile\u0000"u8) || StartsWith(header, [0x7F, 0x45, 0x4C, 0x46]))
            return new MagicResult("ELF", FileCategory.Executable, "application/x-elf", 4);
        if (StartsWith(header, [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00]))
            return new MagicResult("LNK", FileCategory.Shortcut, "application/x-ms-shortcut", 8);
        if (StartsWith(header, "EVF\u0000"u8) || StartsWith(header, "ElfFile\u0000"u8))
            return new MagicResult("EVTX", FileCategory.System, "application/octet-stream", 4);
        if (StartsWith(header, "d"u8) && header.Length > 6 && header[1] == 0x00 && header[2] == 0x00 &&
            header[3] == 0x00 && header[4] == 0x00 && header[5] == 0x00)
            return new MagicResult("DWG", FileCategory.Other, "image/vnd.dwg", 6);

        // --- 文本（无 BOM 的 UTF-8/ASCII 启发式：可打印字节占比高且无空字节）
        if (LooksLikeText(header))
            return new MagicResult("TEXT", FileCategory.Text, "text/plain", Math.Min(header.Length, 64));

        return null;
    }

    /// <summary>文本启发式：前若干字节中可打印字符（含常见多字节 UTF-8 前导字节）占比 ≥ 90%。</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;

        // UTF-8 BOM / UTF-16 BOM 直接判为文本
        if (StartsWith(data, [0xEF, 0xBB, 0xBF])) return true;
        if (StartsWith(data, [0xFF, 0xFE]) || StartsWith(data, [0xFE, 0xFF])) return true;

        var sample = data.Length > 512 ? data[..512] : data;
        var printable = 0;
        foreach (var b in sample)
        {
            if (b == 0) return false;                       // 含 NUL 视为二进制
            if (b >= 0x20 && b < 0x7F) printable++;         // ASCII 可打印
            else if (b == 0x09 || b == 0x0A || b == 0x0D) printable++;
            else if (b >= 0x80) printable++;                // 多字节 UTF-8 / GBK 余字节
        }
        return (double)printable / sample.Length >= 0.90;
    }

    /// <summary>文件头十六进制列表（未识别文件供人工判断）。</summary>
    public static string ToHex(ReadOnlySpan<byte> header, int max = 32)
    {
        var n = Math.Min(max, header.Length);
        return n == 0 ? string.Empty : Convert.ToHexString(header[..n]).Chunk(2)
            .Select(c => new string(c.ToArray()))
            .Aggregate((a, b) => a + " " + b);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
