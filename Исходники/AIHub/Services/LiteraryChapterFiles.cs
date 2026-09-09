using System.IO;
using System.Text;

namespace AIHub.Services;

public static class LiteraryChapterFiles
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static void Write(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return Utf8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static string SafeTitle(string title)
    {
        var cleaned = new string(title.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "Без названия";
        // Leave room for numbering, extension and the project directory.
        return cleaned.Length <= 80 ? cleaned : cleaned[..80].TrimEnd();
    }

    public static string FileName(int chapter, int part, string title) =>
        $"[{chapter:000}{(part == 1 ? "" : "." + part)}] {SafeTitle(title)}.txt";

    public static IReadOnlyList<string> SplitParagraphs(string text, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var parts = new List<string>(); var start = 0;
        while (text.Length - start > limit)
        {
            var boundary = text.LastIndexOf('\n', start + limit - 1, limit);
            if (boundary < start) throw new InvalidOperationException("Literary.Editor.LongParagraph");
            var count = boundary - start + 1;
            parts.Add(text.Substring(start, count)); start += count;
        }
        if (start < text.Length) parts.Add(text[start..]);
        return parts;
    }
}

public sealed class LiteraryChapterIndex
{
    public int Version { get; set; } = 1;
    public string Transaction { get; set; } = "";
    public int AutosaveSeconds { get; set; } = 30;
    public string ActiveId { get; set; } = "";
    public List<LiteraryChapterPart> Parts { get; set; } = [];
}

public sealed class LiteraryChapterPart
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Chapter { get; set; }
    public int Part { get; set; } = 1;
    public string Title { get; set; } = "Без названия";
    public string FileName { get; set; } = "";
    public bool Finished { get; set; }
}

public sealed record LiteraryExportChapter(int Number, string Title, IReadOnlyList<string> Parts);
