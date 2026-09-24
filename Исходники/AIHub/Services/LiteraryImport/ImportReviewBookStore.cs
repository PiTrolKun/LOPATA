using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Atomic whole-book snapshots; original import parts remain available for recovery.</summary>
public sealed class ImportReviewBookStore
{
    private readonly LiteraryProjectLayout _layout;
    private string? _savedHash;
    public string FilePath => Path.Combine(_layout.Root, "Import", "book-review.json");
    public ImportReviewBookStore(string root) => _layout = new(root);

    public FileStream AcquireEditor() => new(Path.Combine(_layout.EnsureFolder("Import"), "book-editor.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public ImportReviewBook Load()
    {
        _layout.EnsurePresent();
        if (File.Exists(FilePath))
        {
            var json = LiteraryChapterFiles.Read(FilePath);
            var book = JsonSerializer.Deserialize<ImportReviewBook>(json, ImportJson.Options)
                ?? throw new InvalidDataException("Literary.Import.Corrupt");
            book.Validate();
            if (book.ProjectId != _layout.ProjectId || book.BaseRevision != BaseRevision())
                throw new IOException("Literary.Import.Changed");
            _savedHash = LiteraryWorkIndex.Revision(json);
            return book;
        }
        if (File.Exists(FilePath + ".bak")) throw new IOException("Literary.Import.Corrupt");
        _savedHash = null;
        var store = new LiteraryChapterStore(_layout.Root); store.Open();
        var result = new ImportReviewBook { ProjectId = _layout.ProjectId, BaseRevision = BaseRevision() };
        var reviews = JsonSerializer.Deserialize<ImportReviewFile>(LiteraryChapterFiles.Read(
            Path.Combine(_layout.Root, "Import", "review.json")), ImportJson.Options)!.Parts.ToDictionary(p => p.PartId);
        var text = new StringBuilder();
        foreach (var group in store.Index.Parts.Where(p => p.Finished && p.Id != store.Index.ActiveId)
            .OrderBy(p => p.Chapter).ThenBy(p => p.Part).GroupBy(p => p.Chapter))
        {
            if (text.Length > 0) text.Append("\n\n");
            result.Headings.Add(new(group.First().Id, text.Length, group.First().Title.Length));
            text.Append(group.First().Title).Append("\n\n");
            var first = true;
            foreach (var part in group)
            {
                if (!first && !part.ExactContinuation) text.Append('\n');
                first = false;
                var content = LiteraryChapterFiles.Read(Path.Combine(_layout.Root, "chapters", part.FileName));
                var review = reviews.GetValueOrDefault(part.Id);
                var spans = review?.Doubts ?? [];
                if (spans.Length > 0 && review!.Revision != LiteraryWorkIndex.Revision(content))
                    spans = [new(0, content.Length, "", "Literary.Import.Changed")];
                foreach (var span in spans)
                    result.Marks.Add(new(Guid.NewGuid().ToString("N"), text.Length + span.Start, span.Length, span.UnitId, span.Reason));
                text.Append(content);
            }
        }
        result.Text = text.ToString(); result.Validate(); return result;
    }

    public void Save(ImportReviewBook book)
    {
        _layout.EnsurePresent(); book.Validate();
        using var lease = new FileStream(Path.Combine(_layout.Root, "Import", "review.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (book.ProjectId != _layout.ProjectId || book.BaseRevision != BaseRevision()
            || (File.Exists(FilePath) ? LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(FilePath)) : null) != _savedHash)
            throw new IOException("Literary.Import.Changed");
        var json = JsonSerializer.Serialize(book, ImportJson.Options);
        if (_savedHash == LiteraryWorkIndex.Revision(json)) return;
        LiteraryChapterFiles.Write(FilePath, json); _savedHash = LiteraryWorkIndex.Revision(json);
    }

    private string BaseRevision()
    {
        var store = new LiteraryChapterStore(_layout.Root); store.Open();
        var index = store.Index; var folder = Path.Combine(_layout.Root, "chapters");
        var reviewPath = Path.Combine(_layout.Root, "Import", "review.json");
        var workspacePath = Path.Combine(_layout.Root, "Import", "workspace.json");
        if (File.Exists(workspacePath))
        {
            var state = JsonSerializer.Deserialize<ImportWorkspaceState>(LiteraryChapterFiles.Read(workspacePath), ImportJson.Options)
                ?? throw new InvalidDataException("Invalid workspace state.");
            if (state.ProjectId != _layout.ProjectId || !Guid.TryParseExact(state.Activation, "N", out _)) throw new InvalidDataException();
            if (state.Ready || index.Transaction == state.Activation)
            {
                // The original review source is immutable after working copies enter the editor.
                folder = Path.Combine(_layout.Root, "Import", "BeforeWorkspace", state.Activation);
                index = JsonSerializer.Deserialize<LiteraryChapterIndex>(LiteraryChapterFiles.Read(Path.Combine(folder, "index.json")))!;
                reviewPath = Path.Combine(folder, "review.json");
            }
        }
        var parts = index.Parts.Where(p => p.Finished && p.Id != index.ActiveId).Select(p => new
        {
            p.Id, p.Chapter, p.Part, p.Title, p.ExactContinuation,
            revision = LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(Path.Combine(folder, p.FileName)))
        });
        return LiteraryWorkIndex.Revision(JsonSerializer.Serialize(parts) + LiteraryChapterFiles.Read(reviewPath));
    }
}
