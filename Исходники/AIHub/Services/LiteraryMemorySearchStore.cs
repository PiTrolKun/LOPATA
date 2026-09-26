using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>One atomically replaced file per completed pass; interrupted temporary files are never read.</summary>
internal sealed class LiteraryMemorySearchStore : IDisposable
{
    private sealed record WindowRecord(string Source, string Kind, string Label, string Coordinate, int Start, int End,
        int CoveredStart, string Hash);
    private sealed record PassRecord(int Version, int Sequence, MemorySearchCursor Start, MemorySearchCursor End,
        WindowRecord[] Windows, MemorySearchFinding[] Findings);
    private readonly FileStream _lease;
    public string Directory { get; }
    public string Key { get; }
    public LiteraryMemorySearchStore(LiteraryMemorySearchRequest request, MemorySearchSource[] sources)
    {
        Key = LiteraryMemorySearchSources.Hash(ParagraphJson.Encode(new { version = 3, request.Fingerprint,
            request.QueryContext, sources, request.Evidence.Receipts }));
        var layout = new LiteraryProjectLayout(request.Directory);
        Directory = layout.EnsureFolder("Dialogs/MemorySearch/" + Key);
        var lockPath = Path.Combine(Directory, "session.lock"); CheckFile(lockPath);
        _lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var manifest = new { Version = 3, Key, request.Fingerprint, request.QueryContext,
                Sources = sources.Select(s => new { s.Id, s.Kind, s.Revision, s.Coordinate, s.Offset, s.Label, s.Materials,
                    Length = s.Text.Length, Hash = LiteraryMemorySearchSources.Hash(s.Text) }), request.Evidence.Receipts };
            var path = Path.Combine(Directory, "manifest.json");
            if (File.Exists(path))
            {
                var existing = Read<JsonElement>(path);
                if (ParagraphJson.Encode(existing) != ParagraphJson.Encode(manifest))
                    throw new InvalidDataException("Foreign or damaged memory search manifest. Original files retained.");
            }
            else Write(path, manifest);
        }
        catch { _lease.Dispose(); throw; }
    }

    public MemorySearchPass[] LoadPasses(MemorySearchSource[] sources)
    {
        var passes = new List<MemorySearchPass>(); var cursor = new MemorySearchCursor(0, 0);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "pass-*.json").Order(StringComparer.Ordinal))
        {
            var record = Read<PassRecord>(file);
            if (record.Version != 3 || record.Windows is null || record.Findings is null)
                throw new InvalidDataException("Invalid memory search checkpoint format.");
            var windows = record.Windows.Select(w =>
            {
                var source = sources.SingleOrDefault(s => s.Id == w.Source);
                if (source is null || w.Start < source.Offset || w.End < w.Start || (long)w.End > (long)source.Offset + source.Text.Length)
                    throw new InvalidDataException("Invalid saved memory source range.");
                var text = source.Text[(w.Start - source.Offset)..(w.End - source.Offset)];
                if (LiteraryMemorySearchSources.Hash(text) != w.Hash)
                    throw new InvalidDataException("Saved memory source range no longer matches the source.");
                return new MemorySearchWindow(w.Source, w.Kind, w.Label, w.Coordinate, w.Start, w.CoveredStart, text);
            }).ToArray();
            var pass = new MemorySearchPass(1, record.Sequence, record.Start, record.End, windows, record.Findings);
            if (pass.Sequence != passes.Count + 1 || Path.GetFileName(file) != PassName(pass.Sequence))
                throw new InvalidDataException("Memory search checkpoint sequence is damaged. Original files retained.");
            LiteraryMemorySearchSources.Validate(pass, sources, cursor);
            passes.Add(pass); cursor = pass.End;
        }
        return passes.ToArray();
    }

    public void Save(MemorySearchPass pass) => Write(Path.Combine(Directory, PassName(pass.Sequence)),
        new PassRecord(3, pass.Sequence, pass.Start, pass.End, pass.Windows.Select(w => new WindowRecord(w.Source,
            w.Kind, w.Label, w.Coordinate, w.Start, w.Start + w.Text.Length, w.CoveredStart, LiteraryMemorySearchSources.Hash(w.Text))).ToArray(), pass.Findings));
    private static string PassName(int number) => "pass-" + number.ToString("D8") + ".json";
    public MemorySearchNode? LoadMerge(string id)
    {
        var path = Path.Combine(Directory, "merge-" + id + ".json");
        return File.Exists(path) ? Read<MemorySearchNode>(path) : null;
    }
    public void SaveMerge(MemorySearchNode result) => Write(Path.Combine(Directory, "merge-" + result.Id + ".json"), result);
    public MemoryReviewResult? LoadReview(string id)
    {
        var path = Path.Combine(Directory, "review-" + id + ".json");
        return File.Exists(path) ? Read<MemoryReviewResult>(path) : null;
    }
    public void SaveReview(MemoryReviewResult result) => Write(Path.Combine(Directory, "review-" + result.Id + ".json"), result);
    public void SaveFailure(string stage, int sequence, int attempts, Exception error) =>
        Write(Path.Combine(Directory, "failure.json"), new { version = 1, stage, sequence, attempts,
            status = "failed_not_empty", error = error.Message, utc = DateTimeOffset.UtcNow });
    public void Complete(int passes, int findings) => Write(Path.Combine(Directory, "complete.json"),
        new { version = 1, passes, findings, status = "all_selected_material_ranges_read", utc = DateTimeOffset.UtcNow });

    private static T Read<T>(string path)
    {
        CheckFile(path);
        try { return JsonSerializer.Deserialize<T>(LiteraryChapterFiles.Read(path), ParagraphJson.Options)
            ?? throw new InvalidDataException("Empty memory search checkpoint."); }
        catch (JsonException ex) { throw new InvalidDataException("Damaged memory search checkpoint. Original file retained.", ex); }
    }
    private static void Write(string path, object data)
    {
        CheckFile(path); CheckFile(path + ".bak");
        LiteraryChapterFiles.Write(path, ParagraphJson.Encode(data));
    }
    private static void CheckFile(string path)
    {
        LiteraryProjectLayout.CheckTreePath(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked memory search state is unsupported.");
    }
    public void Dispose() => _lease.Dispose();
}
