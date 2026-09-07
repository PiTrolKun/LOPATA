using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Durable independent-image workflow. Only committed materials are reusable.</summary>
public sealed class ImageBatchProcessor(ImageBatchStore store, IImageBatchModel model)
{
    public async Task RunAsync(ImageBatchJob job, IProgress<ImageBatchProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(store.DirectoryFor(job));
        using var lease = AcquireLease(store.DirectoryFor(job));
        if (!job.Started) for (var i = 0; i < job.Items.Count; i++) job.Items[i].Position = i + 1;
        job.Started = true; job.Status = "running"; job.Error = ""; store.Save(job);
        void Report(string stage) => progress?.Report(new(job.Items.Count(i => i.Status is "analyzed" or "ready" or "error"),
            job.Items.Count, job.Items.Count(i => i.Status == "ready"), job.Items.Count(i => i.Status == "error"), stage));
        try
        {
            // A restart reuses saved analysis even if its temporary source was cleaned after a crash.
            // Settings and order become immutable after the first start.
            foreach (var item in job.Items)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var analysis = store.Read<ImageBatchAnalysis>(job, item.Id, "analysis");
                    if (analysis is null)
                    {
                        item.Status = "running"; item.Error = ""; store.Save(job); Report("analyze");
                        await using var source = File.OpenRead(item.File.SourcePath);
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                        if (!string.Equals(hash, item.File.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Source image changed since selection.");
                        analysis = await Retry(() => model.AnalyzeAsync(item.File, job.Settings, token), item, job, Report, token);
                        if (string.IsNullOrWhiteSpace(analysis.Details) || string.IsNullOrWhiteSpace(analysis.Summary))
                            throw new InvalidDataException("Invalid saved analysis.");
                        store.SaveMaterial(job, item.Id, "analysis", analysis);
                    }
                    if (string.IsNullOrWhiteSpace(analysis.Details) || string.IsNullOrWhiteSpace(analysis.Summary))
                        throw new InvalidDataException("Invalid saved analysis.");
                    item.Status = "analyzed"; item.Error = "";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { item.Status = "error"; item.Error = ex.Message; }
                store.Save(job); Report("analyze");
            }
            var pending = new List<ImageBatchItem>();
            foreach (var item in job.Items.Where(i => i.Status == "analyzed"))
            {
                var saved = store.Read<ImageBatchSection>(job, item.Id, "final");
                if (saved is not null && ImageBatchLanguageGuard.Matches(saved, job.Settings.LanguageCode))
                {
                    Validate(saved, item.Id); item.Status = "ready";
                }
                else pending.Add(item);
            }
            // Fixed small ceiling limits output length; admission is still measured in actual tokens
            // by the runtime. Oversized groups split recursively, without discarding details.
            foreach (var group in pending.Chunk(job.SingleDocument ? 4 : 1))
                await Format(group, job, Report, token);
            token.ThrowIfCancellationRequested();
            Report("save");
            var sections = job.Items.Where(i => i.Status == "ready")
                .Select(i => (Item: i, Section: store.Read<ImageBatchSection>(job, i.Id, "final")!)).ToArray();
            if (sections.Length == 0) throw new InvalidDataException("No completed descriptions.");
            ImageBatchExporter.Save(store.Results(job), sections, job.SingleDocument, job.Settings.LanguageCode);
            job.Status = "completed"; store.Save(job); Report("completed");
        }
        catch (OperationCanceledException) { job.Status = "paused"; store.Save(job); Report("paused"); throw; }
        catch (Exception ex) { job.Status = "failed"; job.Error = ex.Message; store.Save(job); Report("failed"); throw; }
    }

    private async Task Format(ImageBatchItem[] group, ImageBatchJob job, Action<string> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); report("format");
        try
        {
            var data = group.Select(i => (i.Id, store.Read<ImageBatchAnalysis>(job, i.Id, "analysis")!)).ToArray();
            var result = await Retry(async () => {
                var sections = await model.FormatAsync(data, job.Settings, token);
                foreach (var section in sections)
                    if (!ImageBatchLanguageGuard.Matches(section, job.Settings.LanguageCode))
                        throw new InvalidDataException("The description language does not match the requested output language.");
                return sections;
            }, null, job, report, token);
            if (!result.Select(s => s.Id).SequenceEqual(group.Select(i => i.Id))) throw new InvalidDataException("Invalid section order.");
            foreach (var section in result) Validate(section, section.Id);
            foreach (var section in result) store.SaveMaterial(job, section.Id, "final", section);
            foreach (var item in group) { item.Status = "ready"; item.Error = ""; }
        }
        catch (OperationCanceledException) { throw; }
        catch (ImageAnalysisContextExhaustedException) when (group.Length > 1)
        {
            var half = group.Length / 2;
            await Format(group[..half], job, report, token);
            await Format(group[half..], job, report, token);
        }
        catch (Exception ex)
        {
            foreach (var item in group) { item.Status = "error"; item.Error = ex.Message; }
        }
        store.Save(job); report("format");
    }

    private async Task<T> Retry<T>(Func<Task<T>> run, ImageBatchItem? item, ImageBatchJob job, Action<string> report, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (item is not null) { item.Attempts++; store.Save(job); }
            try { return await run(); }
            catch (OperationCanceledException) { throw; }
            catch (ImageAnalysisContextExhaustedException ex) when (!ex.OutputTruncated) { throw; }
            catch (Exception) { model.Restart(); if (attempt >= 3) throw; report("retry"); }
        }
    }
    private static void Validate(ImageBatchSection s, string id)
    {
        if (s.Id != id || string.IsNullOrWhiteSpace(s.Title) || s.Paragraphs is null || s.Paragraphs.Length == 0 || s.Paragraphs.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid saved section.");
    }
    private static FileStream AcquireLease(string directory)
    {
        try { return new FileStream(Path.Combine(directory, "processing.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new ImageBatchBusyException(ex); }
    }
}

public sealed class ImageBatchBusyException(Exception inner) : IOException("This batch is already being processed, or its lock is unavailable.", inner);
