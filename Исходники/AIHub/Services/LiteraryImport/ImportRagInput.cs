using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportRagSection(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("kind")] string Kind);
public sealed record ImportRagSource(string File, string Snapshot, string Sha256);
public sealed record ImportRagInput(string Area, string Revision, ImportRagSection[] Sections, ImportRagSource[] Sources)
{
    public long Characters => Sections.Sum(s => (long)s.Text.Length);
    public bool ShowTips => Characters > 20_000;

    public static ImportRagInput Book(string root)
    {
        // Never reconstruct the current book from the obsolete import TXT parts.
        if (!System.IO.File.Exists(Path.Combine(root, "Import", "book-review.json")))
            throw new InvalidDataException("Confirmed book snapshot is missing.");
        var book = new ImportReviewBookStore(root).Load();
        if (string.IsNullOrWhiteSpace(book.Text)) throw new InvalidDataException("Confirmed book is empty.");
        return new("Book", ImportSession.Hash(book.Text), [new(book.ProjectId, "book", book.Text, "project")], []);
    }

    public static ImportRagInput Reference(LiteraryProjectLayout layout, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sections = new List<ImportRagSection>();
        var sources = new List<ImportRagSource>();
        var snapshots = layout.EnsureFolder("Rag/ImportSources");
        for (var i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(paths[i]);
            var pointer = Path.Combine(snapshots, ImportSession.Hash(Path.GetFullPath(paths[i]).ToUpperInvariant()) + ".json");
            var originalExists = System.IO.File.Exists(paths[i]);
            if (originalExists && new FileInfo(paths[i]).Length > 100L * 1024 * 1024)
                throw new InvalidDataException("Reference exceeds the file size limit.");
            // Content-addressed snapshots are retained for resuming and never overwrite source files.
            var saved = !originalExists && System.IO.File.Exists(pointer)
                ? JsonSerializer.Deserialize<ImportRagSource>(LiteraryChapterFiles.Read(pointer)) : null;
            var hash = originalExists ? ImportSession.HashFile(paths[i]) : saved?.Sha256
                ?? throw new FileNotFoundException("Reference and its snapshot are unavailable.");
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid snapshot identity.");
            var directory = layout.EnsureFolder(Path.Combine("Rag/ImportSources", hash));
            var copy = Path.Combine(directory, name);
            if (!System.IO.File.Exists(copy) || ImportSession.HashFile(copy) != hash)
            {
                if (!originalExists) throw new InvalidDataException("Reference snapshot is damaged.");
                var temporary = copy + ".tmp";
                System.IO.File.Copy(paths[i], temporary, true);
                if (ImportSession.HashFile(temporary) != hash) throw new IOException("Reference changed while copying.");
                System.IO.File.Move(temporary, copy, true);
            }
            var source = $"{i + 1:D4}_" + name;
            sections.AddRange(LiterarySourceReader.Read(copy, source, ct).Select(s => new ImportRagSection(s.Source, s.Section, s.Text, "reference")));
            if (sections.Sum(s => (long)s.Text.Length) > LiterarySourceReader.MaxCharacters)
                throw new InvalidDataException("Combined reference is too large.");
            var record = new ImportRagSource(name, Path.GetRelativePath(layout.Root, copy), hash);
            sources.Add(record);
            LiteraryChapterFiles.Write(pointer, JsonSerializer.Serialize(record));
        }
        return new("Reference", ImportSession.Hash(JsonSerializer.Serialize(sources)), sections.ToArray(), sources.ToArray());
    }
}
