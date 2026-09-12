using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Resumable extraction. Every proposed record requires an explicit review decision.</summary>
public sealed class LiteraryJellyPreparation(LiteraryProjectLayout layout, Func<string, CancellationToken, Task<string>> extract, string executor = "runeweaver")
{
    public async Task<bool> PrepareAsync(Func<LiteraryJellyBatch, Func<IReadOnlyList<LiteraryJellyFact>, Task>, Task<bool>> review,
        IProgress<LiteraryPreparationProgress> progress, CancellationToken token)
    {
        using var log = new LiteraryRequestDiagnostics("JellyPreparation", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
        try
        {
            LiteraryJellyInstallation.ValidateMode(executor);
            log.Write("executor", executor);
            var chapters = new LiteraryChapterStore(layout.Root); chapters.Open();
            var snapshot = LiteraryEditorSnapshot.Capture(layout.ProjectId, layout.Root, chapters.Index, chapters.Load(), false);
            var sources = snapshot.Sources.Where(s => s.Id != snapshot.ActiveId).ToArray();
            var memory = new LiteraryJellyStore(layout);
            for (var index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested(); layout.EnsurePresent();
                var source = sources[index];
                var text = await Task.Run(() => LiteraryChapterFiles.Read(Path.Combine(layout.Root, "chapters", source.FileName)), token);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var revision = LiteraryWorkIndex.Revision(text);
                var batch = await Task.Run(() => memory.Find(source.Id, revision), token);
                if (batch?.Status == "confirmed") continue;
                log.Write("source", new { source.Id, source.Number, revision, text });
                if (batch is null)
                {
                    var chunks = LiteraryJellyContract.Chunks(text).ToArray(); var facts = new List<LiteraryJellyFact>();
                    var folder = layout.EnsureFolder(Path.Combine("Jelly", "Staging", Guid.Parse(source.Id).ToString("N"), revision,
                        executor == "runeweaver" ? "" : executor + "-v1"));
                    for (var n = 0; n < chunks.Length; n++)
                    {
                        token.ThrowIfCancellationRequested();
                        progress.Report(new("JellyExtracting", 100.0 * (index + (double)n / chunks.Length) / Math.Max(1, sources.Length), $"[{source.Number}] · {n + 1}/{chunks.Length}"));
                        var checkpoint = Path.Combine(folder, $"{n:000}.json");
                        LiteraryJellyFact[]? rows = null;
                        if (File.Exists(checkpoint)) rows = JsonSerializer.Deserialize<LiteraryJellyFact[]>(LiteraryChapterFiles.Read(checkpoint));
                        if (rows is null)
                        {
                            Exception? last = null;
                            for (var attempt = 1; attempt <= 3; attempt++)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    var raw = await extract(chunks[n], token);
                                    log.Write("extraction", new { chunk = n, attempt, raw });
                                    rows = LiteraryJellyContract.Parse(raw); break;
                                }
                                catch (Exception ex) when (!token.IsCancellationRequested)
                                { last = ex; log.Write("extraction_failure", new { chunk = n, attempt, type = ex.GetType().Name, ex.Message }); }
                            }
                            if (rows is null) throw new IOException("Memory extraction failed after three attempts.", last);
                            LiteraryChapterFiles.Write(checkpoint, JsonSerializer.Serialize(rows));
                        }
                        facts.AddRange(rows);
                    }
                    batch = new(Guid.NewGuid().ToString("N"), source.Id, source.Number, revision, text, facts.ToArray());
                    await Task.Run(() => memory.Stage(batch), token);
                }
                log.Write("proposals", batch);
                progress.Report(new("JellyReview", -1, $"[{source.Number}]"));
                var confirmed = await review(batch, async decisions =>
                {
                    token.ThrowIfCancellationRequested();
                    log.Write("user_decisions", new { batch.Id, decisions });
                    try { await Task.Run(() => memory.Confirm(batch, decisions), token); }
                    catch (Exception ex) { log.Write("commit_failure", new { batch.Id, ex.Message }); throw; }
                    log.Write("committed", new { batch.Id, accepted = decisions.Count(f => f.Accepted), excluded = decisions.Count(f => !f.Accepted) });
                });
                token.ThrowIfCancellationRequested();
                if (!confirmed) { log.Write("review_deferred", new { batch.Id }); return false; }
            }
            return true;
        }
        catch (Exception ex) { log.Write("failure", new { type = ex.GetType().Name, ex.Message }); throw; }
    }
}
