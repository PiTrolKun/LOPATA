using System.IO;
using System.Text.Json;
using AIHub.Services.LiteraryImport;

namespace AIHub.Services;

/// <summary>Resumable extraction. Every proposed record requires an explicit review decision.</summary>
public sealed class LiteraryJellyPreparation(LiteraryProjectLayout layout, Func<string, CancellationToken, Task<string>> extract, string executor = "runeweaver")
{
    public async Task<bool> PrepareAsync(Func<LiteraryJellyBatch, Func<IReadOnlyList<LiteraryJellyFact>, Task>, Task<bool>> review,
        IProgress<LiteraryPreparationProgress> progress, CancellationToken token, bool prepareAllPending = false,
        string? mode = null, int technicalAttempts = 3)
    {
        if (mode is not (null or "auto" or "manual") || technicalAttempts < 1 || technicalAttempts > 4)
            throw new ArgumentException("Invalid preparation options.");
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
                // Memory facts are extracted after the whole imported part has been reviewed.
                // This avoids a partial fact batch being mistaken for a complete, confirmed one.
                if (ImportEligibility.Review(layout.Root, source.Id)?.Doubts.Length > 0) continue;
                var revision = LiteraryWorkIndex.Revision(text);
                var batch = await Task.Run(() => memory.Find(source.Id, revision), token);
                if (batch?.Status == "confirmed") continue;
                log.Write("source", new { source.Id, source.Number, revision, text });
                if (batch is null)
                {
                    var chunks = ImportEligibility.Allowed(layout.Root, source.Id, text).SelectMany(LiteraryJellyContract.Chunks).ToArray();
                    if (chunks.Length == 0) continue;
                    var facts = new List<LiteraryJellyFact>();
                    var folder = layout.EnsureFolder(Path.Combine("Jelly", "Staging", Guid.Parse(source.Id).ToString("N"), revision,
                        executor == "runeweaver" ? "" : executor + "-v1", ImportSession.Hash(ImportEligibility.Fingerprint(layout.Root, source.Id))));
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
                            for (var attempt = 1; attempt <= technicalAttempts; attempt++)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    var raw = mode is not null && ImportJellyResponse.IsSeparator(chunks[n]) ? "[]" : await extract(chunks[n], token);
                                    log.Write("extraction", new { chunk = n, attempt, raw });
                                    rows = mode is null ? LiteraryJellyContract.Parse(raw) : ImportJellyResponse.Parse(raw); break;
                                }
                                catch (Exception ex) when (!token.IsCancellationRequested)
                                { last = ex; log.Write("extraction_failure", new { chunk = n, attempt, type = ex.GetType().Name, ex.Message }); }
                            }
                            if (rows is null) throw new IOException("Memory extraction failed after technical attempts.", last);
                            LiteraryChapterFiles.Write(checkpoint, JsonSerializer.Serialize(rows));
                        }
                        facts.AddRange(rows);
                    }
                    batch = new(Guid.NewGuid().ToString("N"), source.Id, source.Number, revision, text, facts.ToArray());
                    await Task.Run(() => memory.Stage(batch), token);
                }
                if(batch.Facts.Length==0) { await Task.Run(()=>memory.ConfirmEmpty(batch),token); log.Write("empty_batch_completed",new {batch.Id}); continue; }
                if(mode=="auto")
                {
                    await Task.Run(()=>memory.ConfirmPrepared(batch,batch.Facts,"auto"),token);
                    log.Write("auto_saved",new {batch.Id, count=batch.Facts.Length}); continue;
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
                if (!confirmed) { log.Write("review_deferred", new { batch.Id }); if (!prepareAllPending) return false; }
            }
            return true;
        }
        catch (Exception ex) { log.Write("failure", new { type = ex.GetType().Name, ex.Message }); throw; }
    }
}
