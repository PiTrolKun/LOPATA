using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services.Books;
using AngleSharp.Dom;

namespace AIHub.Services.MusicLibrary;

public static class LyricLibraryImport
{
    public static readonly string[] Extensions = [".txt", ".md", ".markdown", ".html", ".htm", ".docx", ".epub", ".fb2", ".json", ".jsonl"];
    public static LyricEntry[] Read(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Music.Library.TooLarge");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new InvalidDataException("Music.Library.Format");
        if (extension == ".json")
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("kind").GetString() != "lopata.lyrics-library")
                throw new InvalidDataException("Music.Library.InvalidRecord");
            var entries = root.GetProperty("entries").Deserialize<LyricEntry[]>() ?? [];
            foreach (var entry in entries) entry.Validate(); return entries;
        }
        if (extension == ".jsonl")
        {
            var entries = new List<LyricEntry>();
            foreach (var line in File.ReadLines(path, Encoding.UTF8).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                token.ThrowIfCancellationRequested(); using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                string Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                string List(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                    ? string.Join("; ", v.EnumerateArray().Select(s => s.GetString())) : Get(name);
                var entry = new LyricEntry { Title = Get("title"), Text = Get("lyrics"), Author = Get("author"), Artist = Get("artist"),
                    Genre = List("genres") is { Length: > 0 } genres ? genres : Get("genre"),
                    Style = List("styles") is { Length: > 0 } styles ? styles : Get("style"), Notes = Get("notes"),
                    Tags = List("topics") is { Length: > 0 } topics ? topics : List("tags"),
                    Language = Get("language") is { Length: > 0 } language ? language : "und",
                    Origin = Get("provenance") is { Length: > 0 } origin ? origin : "external import; model history unknown" };
                entry.Validate(); entries.Add(entry);
            }
            return entries.ToArray();
        }
        // The common reader supplies safe archive/markup handling, not the library's identity/schema.
        var book = BookConversionReader.Read(path, new(), token);
        var texts = book.Parts.Select(part => {
            var doc = BookMarkup.Parse(part.Html); BookMarkup.Sanitize(doc);
            foreach (var br in doc.QuerySelectorAll("br").ToArray()) br.ReplaceWith(doc.CreateTextNode("\n"));
            foreach (var block in doc.QuerySelectorAll("p,div,h1,h2,h3,h4,li,tr").ToArray()) block.AppendChild(doc.CreateTextNode("\n"));
            return doc.Body?.TextContent ?? "";
        });
        var text = string.Join("\n\n", texts);
        if (text.Length == 0) throw new InvalidDataException("Music.Library.InvalidRecord");
        return [new() { Title = string.IsNullOrWhiteSpace(book.Title) ? Path.GetFileNameWithoutExtension(path) : book.Title,
            Author = book.Author, Text = text, Language = book.Language is { Length: 2 or 3 } ? book.Language : "und",
            Origin = "external document; model history unknown" }];
    }
    public static LyricSaveResult Merge(LyricLibraryStore store, LyricEntry entry)
    {
        var existing = store.Find(entry.Id);
        if (existing?.TextHash == entry.TextHash) return new(existing, true);
        return store.Save(entry with { Revision = 0, Id = existing is null ? entry.Id : Guid.NewGuid().ToString("N"),
            PreviousVersionId = existing?.Id ?? entry.PreviousVersionId });
    }
    public static void Export(LyricLibraryStore store, string path)
    {
        if (File.Exists(path)) throw new IOException("Music.Library.ExportExists");
        var snapshot = store.Snapshot();
        var provenance = new OutputProvenance { Scenario = "music/personal-lyrics-library/export", ModelHistory = "recorded-per-entry" };
        var temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp.json";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new { schema = 1, kind = "lopata.lyrics-library", provenance,
                    entries = snapshot.Entries }, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(true);
            }
            var restored = Read(temporary, CancellationToken.None);
            if (!restored.Select(e => (e.Id, e.Revision, e.TextHash)).SequenceEqual(
                    snapshot.Entries.Select(e => (e.Id, e.Revision, e.TextHash))))
                throw new InvalidDataException("Music.Library.InvalidRecord");
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
