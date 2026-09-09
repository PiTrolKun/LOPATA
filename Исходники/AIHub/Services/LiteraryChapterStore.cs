using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Plain text chapters with an atomic index commit and a recoverable creation journal.</summary>
public sealed class LiteraryChapterStore
{
    public static IReadOnlyList<int> AutosaveIntervals { get; } = Array.AsReadOnly(new[] { 10, 20, 30, 40, 50, 60, 90, 120, 150, 180 });
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root, _indexPath, _journalPath;
    public LiteraryChapterIndex Index { get; private set; } = new();
    public LiteraryChapterPart Active => Index.Parts.Single(p => p.Id == Index.ActiveId);
    public string FilePath => Resolve(Active.FileName);
    public DateTime LastSaved => File.GetLastWriteTime(FilePath);
    public bool RecoveryAvailable => File.Exists(_indexPath + ".bak") || Index.Parts.Any(p => File.Exists(Resolve(p.FileName) + ".bak"));

    public LiteraryChapterStore(string directory)
    {
        _root = Path.Combine(Path.GetFullPath(directory), "chapters");
        _indexPath = Path.Combine(_root, "index.json"); _journalPath = Path.Combine(_root, "pending.json");
    }

    private string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid chapter file name.");
        return Path.Combine(_root, name);
    }

    private FileStream Enter()
    {
        Directory.CreateDirectory(_root);
        return new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Open()
    {
        using var lease = Enter();
        RecoverTransaction();
        if (File.Exists(_indexPath)) Index = ReadIndex();
        else
        {
            if (File.Exists(_indexPath + ".bak")) throw new InvalidDataException("Chapter index is missing; a backup exists.");
            var legacy = Path.Combine(Path.GetDirectoryName(_root)!, "working-draft.txt");
            var text = File.Exists(legacy) ? LiteraryChapterFiles.Read(legacy) : "";
            var first = NewPart(1, 1, "Без названия");
            var next = new LiteraryChapterIndex { ActiveId = first.Id, Parts = [first] };
            Commit(next, new Dictionary<string, string> { [first.FileName] = text });
            // Keep the legacy source as a migration backup, never re-import it after index creation.
        }
        _ = Load();
    }

    private LiteraryChapterIndex ReadIndex()
    {
        var state = JsonSerializer.Deserialize<LiteraryChapterIndex>(LiteraryChapterFiles.Read(_indexPath), Json)
            ?? throw new InvalidDataException("Empty chapter index.");
        Validate(state); return state;
    }

    private void Validate(LiteraryChapterIndex state)
    {
        if (state.Version != 1 || state.Parts is null || !AutosaveIntervals.Contains(state.AutosaveSeconds)
            || state.Parts.Count(p => p.Id == state.ActiveId) != 1
            || state.Parts.Select(p => p.Id).Distinct().Count() != state.Parts.Count
            || state.Parts.Select(p => (p.Chapter, p.Part)).Distinct().Count() != state.Parts.Count
            || state.Parts.Select(p => p.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Parts.Count)
            throw new InvalidDataException("Invalid chapter index.");
        foreach (var part in state.Parts)
        {
            if (part.Chapter < 1 || part.Part < 1 || part.Title is null || part.FileName != LiteraryChapterFiles.FileName(part.Chapter, part.Part, part.Title))
                throw new InvalidDataException("Invalid chapter metadata.");
            _ = Resolve(part.FileName);
        }
    }

    public string Load() => LiteraryChapterFiles.Read(FilePath);
    public void Save(string text)
    {
        using var lease = Enter(); CheckUnchanged(); LiteraryChapterFiles.Write(FilePath, text);
    }

    private void CheckUnchanged()
    {
        RecoverTransaction();
        if (ReadIndex().Transaction != Index.Transaction) throw new IOException("Project changed in another editor. Reopen it before saving.");
    }

    public void SetAutosave(int seconds)
    {
        if (!AutosaveIntervals.Contains(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var lease = Enter(); CheckUnchanged();
        var next = Clone(); next.AutosaveSeconds = seconds; Commit(next, []);
    }

    public void Finish()
    {
        using var lease = Enter(); CheckUnchanged();
        var next = Clone();
        foreach (var part in next.Parts.Where(p => p.Chapter == Active.Chapter)) part.Finished = true;
        var added = NewPart(checked(next.Parts.Max(p => p.Chapter) + 1), 1, "Без названия");
        next.Parts.Add(added); next.ActiveId = added.Id;
        Commit(next, new Dictionary<string, string> { [added.FileName] = "" });
    }

    public void Continue(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0 || texts.Any(t => t.Length > LiteraryModelPolicy.DraftCharacters)) throw new ArgumentException("Invalid continuation size.");
        using var lease = Enter(); CheckUnchanged();
        var next = Clone(); var number = next.Parts.Where(p => p.Chapter == Active.Chapter).Max(p => p.Part);
        var writes = new Dictionary<string, string>();
        foreach (var text in texts)
        {
            var part = NewPart(Active.Chapter, checked(++number), Active.Title);
            next.Parts.Add(part); next.ActiveId = part.Id; writes.Add(part.FileName, text);
        }
        Commit(next, writes);
    }

    public void Rename(string title)
    {
        using var lease = Enter(); CheckUnchanged();
        title = LiteraryChapterFiles.SafeTitle(title);
        var next = Clone(); var writes = new Dictionary<string, string>(); var retired = new List<string>();
        foreach (var part in next.Parts.Where(p => p.Chapter == Active.Chapter))
        {
            var name = LiteraryChapterFiles.FileName(part.Chapter, part.Part, title);
            if (string.Equals(name, part.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            var source = Resolve(part.FileName);
            writes.Add(name, LiteraryChapterFiles.Read(source)); retired.Add(part.FileName);
            writes.Add(name + ".bak", LiteraryChapterFiles.Read(File.Exists(source + ".bak") ? source + ".bak" : source));
            if (File.Exists(source + ".bak")) retired.Add(part.FileName + ".bak");
            part.Title = title; part.FileName = name;
        }
        Commit(next, writes, retired);
    }

    public IReadOnlyList<LiteraryExportChapter> Snapshot()
    {
        using var lease = Enter(); CheckUnchanged();
        return Index.Parts.OrderBy(p => p.Chapter).ThenBy(p => p.Part).GroupBy(p => p.Chapter)
            .Select(g => new LiteraryExportChapter(g.Key, g.First().Title, g.Select(p => LiteraryChapterFiles.Read(Resolve(p.FileName))).ToArray()))
            .Where(c => c.Parts.Any(t => !string.IsNullOrWhiteSpace(t))).ToArray();
    }

    public void RestoreBackup()
    {
        using var lease = Enter();
        try { Index = ReadIndex(); }
        catch (Exception ex) when (ex is IOException or JsonException or System.Text.DecoderFallbackException)
        {
            var backup = LiteraryChapterFiles.Read(_indexPath + ".bak");
            var state = JsonSerializer.Deserialize<LiteraryChapterIndex>(backup, Json) ?? throw new InvalidDataException();
            Validate(state);
            // Preserve damaged primary separately; don't overwrite the only known good backup.
            if (File.Exists(_indexPath)) File.Copy(_indexPath, _indexPath + ".damaged-" + Guid.NewGuid().ToString("N"));
            var temporary = _indexPath + ".restore-" + Guid.NewGuid().ToString("N");
            LiteraryChapterFiles.Write(temporary, backup);
            File.Move(temporary, _indexPath, true); Index = state;
            foreach (var part in Index.Parts.Where(p => !File.Exists(Resolve(p.FileName))))
            {
                var recovery = Path.Combine(_root, "recovery");
                var source = Directory.Exists(recovery) ? Directory.GetFiles(recovery).Where(p => Path.GetFileName(p).EndsWith("-" + part.FileName, StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
                if (source is not null) File.Copy(source, Resolve(part.FileName));
            }
            // A recovered index may already point to an intact text file.
            try { _ = Load(); return; } catch (Exception error) when (error is IOException or System.Text.DecoderFallbackException) { }
        }
        var text = LiteraryChapterFiles.Read(FilePath + ".bak");
        LiteraryChapterFiles.Write(FilePath, text);
    }

    private LiteraryChapterIndex Clone() => JsonSerializer.Deserialize<LiteraryChapterIndex>(JsonSerializer.Serialize(Index, Json), Json)!;
    private static LiteraryChapterPart NewPart(int chapter, int part, string title) => new()
    { Chapter = chapter, Part = part, Title = title, FileName = LiteraryChapterFiles.FileName(chapter, part, title) };

    private sealed class Journal
    {
        public string Transaction { get; set; } = "";
        public Dictionary<string, string> Writes { get; set; } = [];
        public List<string> Retired { get; set; } = [];
    }

    private void Commit(LiteraryChapterIndex next, Dictionary<string, string> writes, List<string>? retired = null)
    {
        next.Transaction = Guid.NewGuid().ToString("N"); Validate(next);
        foreach (var name in writes.Keys) if (File.Exists(Resolve(name))) throw new IOException("Chapter file already exists: " + name);
        var journal = new Journal { Transaction = next.Transaction, Writes = writes, Retired = retired ?? [] };
        LiteraryChapterFiles.Write(_journalPath, JsonSerializer.Serialize(journal, Json));
        try
        {
            foreach (var (name, text) in writes) LiteraryChapterFiles.Write(Resolve(name), text);
            LiteraryChapterFiles.Write(_indexPath, JsonSerializer.Serialize(next, Json));
        }
        catch { RecoverTransaction(); throw; }
        Index = next;
        // The index is the commit point. Cleanup failure must not report an already committed paste as failed.
        try { RecoverTransaction(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void RecoverTransaction()
    {
        if (!File.Exists(_journalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(LiteraryChapterFiles.Read(_journalPath), Json) ?? throw new InvalidDataException();
        var committed = File.Exists(_indexPath) && ReadIndex().Transaction == journal.Transaction;
        if (!committed)
        {
            foreach (var (name, text) in journal.Writes)
            {
                var path = Resolve(name);
                if (!File.Exists(path)) continue;
                if (LiteraryChapterFiles.Read(path) != text) throw new IOException("Uncommitted chapter changed externally: " + name);
                File.Delete(path);
            }
        }
        // Retain old names after rename as recovery copies, outside the reader-visible chapters.
        else foreach (var name in journal.Retired)
        {
            var path = Resolve(name);
            if (!File.Exists(path)) continue;
            var archive = Path.Combine(_root, "recovery"); Directory.CreateDirectory(archive);
            File.Move(path, Path.Combine(archive, journal.Transaction + "-" + name));
        }
        File.Delete(_journalPath);
    }
}
