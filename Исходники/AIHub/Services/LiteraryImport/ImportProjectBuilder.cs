using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportSpan(int Start, int Length, string UnitId, string Reason);
public sealed record ImportPartReview(string PartId, string Revision, ImportSpan[] Doubts);
public sealed record ImportReviewFile(int Version, string SessionId, ImportPartReview[] Parts);
public sealed record ImportAllowedSpan(int Start, string Text);

public static class ImportProjectBuilder
{
    public static LiteraryProjectEntry Build(ImportSession session, ImportInput input, ImportDecision[] decisions,
        LiteraryProjectStore store, string parent, string name, string genre, string language)
    {
        var existing = store.Load().Projects.FirstOrDefault(p => p.Id == session.State.ProjectId);
        if (existing is not null)
        {
            if (LiteraryProjectStore.ReadProject(existing.ProjectPath).Id != session.State.ProjectId) throw new InvalidDataException();
            EnsureReviewSources(existing.ProjectPath, input);
            session.State.ProjectPath = existing.ProjectPath; session.Save(); return existing;
        }
        if (!LiteraryProjectStore.IsValidProjectName(name)) throw new ArgumentException("Literary.Import.MetadataRequired");
        var destination = Path.GetFullPath(Path.Combine(parent, name));
        if (session.State.PlannedPath.Length > 0 && !string.Equals(session.State.PlannedPath, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Literary.Import.LockedSelection");
        session.State.PlannedPath = destination; session.State.Genre = genre; session.Save();
        if (Directory.Exists(destination))
        {
            var recovered = store.RegisterRecoveredImport(destination, session.State.ProjectId, session.State.Id);
            EnsureReviewSources(recovered.ProjectPath, input);
            session.State.ProjectPath = recovered.ProjectPath; session.Save(); return recovered;
        }
        var selected = decisions.Where(d => d.Kind is "MAIN" or "DOUBT").ToArray();
        var byId = selected.ToDictionary(d => d.Id);
        if (selected.Length == 0) throw new InvalidDataException("Literary.Import.NoBook");
        ImportDisk.Require(parent, input.Units.Sum(u => (long)u.Text.Length) * 8);
        var project = new LiteraryProject { Id = session.State.ProjectId, ProjectName = name, WorkTitle = session.State.SelectedProject,
            CustomGenres = genre, LanguageCode = language };
        // Group adjacent units; an explicitly repeated chapter title does not reorder source history.
        var chapters = new List<(string Title, List<(ImportUnit Unit, ImportDecision Decision)> Units)>();
        foreach (var unit in input.Units)
        {
            if (!byId.TryGetValue(unit.Id, out var decision)) continue;
            var title = string.IsNullOrWhiteSpace(decision.Chapter) ? session.State.SelectedProject : decision.Chapter;
            if (chapters.Count == 0 || chapters[^1].Title != title) chapters.Add((title, []));
            chapters[^1].Units.Add((unit, decision));
        }
        session.AddJson("project-plan", new { project, chapters = chapters.Select(c => new { c.Title, ids = c.Units.Select(u => u.Unit.Id) }) });
        var entry = store.Create(parent, project, [], initializeProject: root =>
        {
            var layout = new LiteraryProjectLayout(root); layout.Initialize();
            var index = new LiteraryChapterIndex(); var reviews = new List<ImportPartReview>(); var number = 0;
            foreach (var chapter in chapters)
            {
                number++; var text = ""; var spans = new List<ImportSpan>(); ImportUnit? previous = null;
                foreach (var pair in chapter.Units)
                {
                    if (text.Length > 0 && !(previous?.Message == pair.Unit.Message && previous.Fragment == pair.Unit.Fragment
                        && previous.Offset + previous.Text.Length == pair.Unit.Offset) && !text.EndsWith('\n')) text += "\n\n";
                    if (pair.Decision.Kind == "DOUBT") spans.Add(new(text.Length, pair.Unit.Text.Length, pair.Unit.Id, pair.Decision.Reason));
                    text += pair.Unit.Text; previous = pair.Unit;
                }
                var offset = 0; var partNumber = 0;
                foreach (var partText in DeepSeekImportReader.SplitExact(text, LiteraryModelPolicy.DraftCharacters))
                {
                    var part = new LiteraryChapterPart { Chapter = number, Part = ++partNumber, Title = chapter.Title,
                        FileName = LiteraryChapterFiles.FileName(number, partNumber, chapter.Title), Finished = true, ExactContinuation = true };
                    index.Parts.Add(part); LiteraryChapterFiles.Write(Path.Combine(root, "chapters", part.FileName), partText);
                    var clipped = spans.Where(s => s.Start < offset + partText.Length && s.Start + s.Length > offset)
                        .Select(s => new ImportSpan(Math.Max(s.Start, offset) - offset,
                            Math.Min(s.Start + s.Length, offset + partText.Length) - Math.Max(s.Start, offset), s.UnitId, s.Reason)).ToArray();
                    reviews.Add(new(part.Id, LiteraryWorkIndex.Revision(partText), clipped)); offset += partText.Length;
                }
            }
            var active = new LiteraryChapterPart { Chapter = number + 1, Title = language == "ru" ? "Продолжение" : "Continuation" };
            active.FileName = LiteraryChapterFiles.FileName(active.Chapter, 1, active.Title);
            index.Parts.Add(active); index.ActiveId = active.Id;
            LiteraryChapterFiles.Write(Path.Combine(root, "chapters", active.FileName), "");
            LiteraryChapterFiles.Write(Path.Combine(root, "chapters", "index.json"), JsonSerializer.Serialize(index, ImportJson.Options));
            var folder = layout.EnsureFolder("Import");
            LiteraryChapterFiles.Write(Path.Combine(folder, "review.json"), JsonSerializer.Serialize(new ImportReviewFile(1, session.State.Id, reviews.ToArray()), ImportJson.Options));
            LiteraryChapterFiles.Write(Path.Combine(folder, "origin.json"), JsonSerializer.Serialize(new { session.State.Id, session.State.SourceHash, sessionFolder = session.Root }));
            EnsureReviewSources(root, input);
            layout.CommitLayout();
        });
        session.State.ProjectPath = entry.ProjectPath; session.State.Stage = "project"; session.Save();
        session.AddJson("project-created", entry); return entry;
    }
    private static void EnsureReviewSources(string root, ImportInput input)
    {
        var path = Path.Combine(root, "Import", "sources.json");
        if (File.Exists(path)) return;
        var review = JsonSerializer.Deserialize<ImportReviewFile>(File.ReadAllText(Path.Combine(root, "Import", "review.json")), ImportJson.Options)!;
        var ids = review.Parts.SelectMany(p => p.Doubts).Select(s => s.UnitId).ToHashSet();
        LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(input.Units.Where(u => ids.Contains(u.Id)), ImportJson.Options));
    }
}

public static class ImportEligibility
{
    public static ImportPartReview? Review(string root, string id)
    {
        var path = Path.Combine(root, "Import", "review.json");
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<ImportReviewFile>(File.ReadAllText(path), ImportJson.Options);
        if (file is null || file.Version != 1) throw new InvalidDataException("Literary.Import.Corrupt");
        return file.Parts.SingleOrDefault(p => p.PartId == id);
    }
    public static IReadOnlyList<string> Allowed(string root, string id, string text)
        => AllowedSpans(root, id, text).Select(s => s.Text).ToArray();
    public static IReadOnlyList<ImportAllowedSpan> AllowedSpans(string root, string id, string text)
    {
        var review = Review(root, id);
        if (review is null || review.Doubts.Length == 0) return [new(0, text)];
        if (review.Revision != LiteraryWorkIndex.Revision(text)) return []; // User edits invalidate offsets; require review.
        var result = new List<ImportAllowedSpan>(); var position = 0;
        foreach (var span in review.Doubts.OrderBy(s => s.Start))
        {
            if (span.Start < position || span.Length < 0 || (long)span.Start + span.Length > text.Length) throw new InvalidDataException("Literary.Import.Corrupt");
            if (span.Start > position) result.Add(new(position, text[position..span.Start]));
            position = span.Start + span.Length;
        }
        if (position < text.Length) result.Add(new(position, text[position..]));
        return result.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToArray();
    }
    public static string Fingerprint(string root, string id)
    {
        var review = Review(root, id); return review is null ? "" : ":import:" + ImportSession.Hash(JsonSerializer.Serialize(review));
    }
    public static void ConfirmPart(string root, string id, string currentText)
        => ImportReviewEdits.Apply(root,id,currentText,currentText);
}
