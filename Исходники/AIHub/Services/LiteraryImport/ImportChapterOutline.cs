using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportOutlineItem(string SourceId, string Title, int Start = 0, int Length = 0, string Revision = "");
public sealed record ImportOutlineResult(ImportOutlineItem[] Items, bool Limited, bool Failed, string Text = "");

/// <summary>Optional, bounded suggestions only. Never repairs or writes a manuscript.</summary>
public static class ImportChapterOutline
{
    public const int MaxBytes = 16 * 1024 * 1024, MaxItems = 2000;
    private static readonly Regex Heading = new(
        @"^(?:(?:глава|chapter|часть|part)\s+(?:№\s*)?(?:[0-9]+|[IVXLCDM]+|первая|вторая|третья|четвёртая|четвертая|пятая|шестая|седьмая|восьмая|девятая|десятая)(?:\b|[.:—–-])|(?:пролог|эпилог|prologue|epilogue)(?:$|\s*[:.—–-]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task<ImportOutlineResult> ReadAsync(string root, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await Task.Run(async () =>
            {
                var ct = timeout.Token;
                var snapshot = Path.Combine(root, "Import", "book-review.json");
                // Never fall back to an older export when the corrected snapshot is damaged.
                if (File.Exists(snapshot))
                {
                    using var input = Open(snapshot);
                    var bytes = await ReadBounded(input, ct);
                    var book = JsonSerializer.Deserialize<ImportReviewBook>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (book is null) throw new InvalidDataException();
                    book.Validate();
                    return FromBook(book, ct);
                }
                if (File.Exists(snapshot + ".bak")) throw new InvalidDataException();
                using var file = Open(Path.Combine(root, "Exports", "Import", "book.docx"));
                using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                if (archive.Entries.Count > 2048) throw new InvalidDataException();
                var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException();
                if (entry.Length > MaxBytes) throw new InvalidDataException();
                using var content = entry.Open();
                var xml = await ReadBounded(content, ct);
                using var memory = new MemoryStream(xml);
                using var reader = XmlReader.Create(memory, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
                var document = XDocument.Load(reader);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var items = new List<ImportOutlineItem>(); var index = 0;
                var manuscript = new StringBuilder(); var limited = false;
                foreach (var p in document.Descendants(w + "p"))
                {
                    ct.ThrowIfCancellationRequested(); index++;
                    var text = string.Concat(p.Descendants(w + "t").Select(t => t.Value)).Trim();
                    var start = manuscript.Length; manuscript.Append(text).Append('\n');
                    var style = (string?)p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val") ?? "";
                    var level = (string?)p.Element(w + "pPr")?.Element(w + "outlineLvl")?.Attribute(w + "val");
                    if (Regex.IsMatch(style, @"^(Heading|Заголовок)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                        || int.TryParse(level, out var n) && n is >= 0 and <= 8 || IsHeading(text))
                    {
                        if (items.Count == MaxItems) { limited = true; continue; }
                        items.Add(new("docx:" + index, ShortTitle(text), start));
                    }
                }
                return Finish(items, manuscript.ToString(), limited);
            }, timeout.Token);
        }
        // This entire optional parser is an isolation boundary for malformed external content.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new([], false, true); }
    }

    public static ImportOutlineResult FromBook(ImportReviewBook book, CancellationToken ct)
    {
        book.Validate();
        var found = new SortedDictionary<int, string>();
        var limited = false;
        foreach (var h in book.Headings.OrderBy(h => h.Start))
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count == MaxItems) { limited = true; break; }
            found.TryAdd(h.Start, ShortTitle(book.HeadingTitle(h)));
        }
        using var lines = new StringReader(book.Text);
        var offset = 0;
        while (lines.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            // ReadLine removes CR/LF; retain exact offsets to merge duplicate anchors, not titles.
            var start = offset; offset += line.Length;
            if (offset < book.Text.Length && book.Text[offset] == '\r') offset++;
            if (offset < book.Text.Length && book.Text[offset] == '\n') offset++;
            var text = line.Trim();
            if (!IsHeading(text) || found.ContainsKey(start)) continue;
            if (found.Count == MaxItems) { limited = true; break; }
            found.Add(start, text);
        }
        var anchors = book.Headings.GroupBy(h => h.Start).ToDictionary(g => g.Key, g => g.First().Id);
        return Finish(found.Select(h => new ImportOutlineItem(anchors.TryGetValue(h.Key, out var id) && !string.IsNullOrWhiteSpace(id)
            ? "heading:" + id : "book:" + h.Key, h.Value, h.Key)).ToList(), book.Text, limited);
    }

    private static ImportOutlineResult Finish(List<ImportOutlineItem> items, string text, bool limited)
    {
        var revision = ImportSession.Hash(text);
        return new(items.Select((item, i) => item with { Revision = revision,
            Length = (i + 1 < items.Count ? items[i + 1].Start : text.Length) - item.Start }).ToArray(), limited, false, text);
    }

    public static string? LinkedText(ImportOutlineResult current, ImportRouteRow row)
    {
        if (current.Failed || string.IsNullOrEmpty(row.SourceId)) return null;
        var item = current.Items.FirstOrDefault(i => i.SourceId == row.SourceId);
        // Stable editor anchors follow edits. Plain-text/DOCX positions require the original revision.
        if (item is null || !row.SourceId.StartsWith("heading:", StringComparison.Ordinal) && item.Revision != row.SourceRevision)
            return null;
        return current.Text.Substring(item.Start, item.Length);
    }

    private static bool IsHeading(string text) => text.Length is > 0 and <= 160 && Heading.IsMatch(text.TrimStart('#', ' ', '*'));
    private static string ShortTitle(string text) => text.Length <= 160 ? text : "";
    private static FileStream Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        if (stream.Length <= MaxBytes) return stream;
        stream.Dispose(); throw new InvalidDataException();
    }
    private static async Task<byte[]> ReadBounded(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (await input.ReadAsync(buffer, ct) is var count && count > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}
