using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One resumable heavy operation. Completed workshop drafts remain in memory until submission.</summary>
public sealed class MusicRhymeWork(Func<IMusicRhymeModel> models)
{
    public const string Kind = "music.rhyme";
    public async Task<MusicRhymeStep> RunAsync(MusicRhymeStep initial, Func<MusicRhymeStep, MusicRhymeStep> changed,
        IProgress<RagBookProgress> progress, CancellationToken token, BackgroundOperationState? restored = null, string? title = null)
    {
        initial.Validate(); var step = initial with { GenerationRequested = true };
        var controller = ApplicationBackgroundOperations.Current;
        var operation = restored ?? new BackgroundOperationState { Kind = Kind, Title = title ?? "Rhyme workshop",
            Project = initial.Request.ProjectId, Private = true, Input = JsonSerializer.SerializeToElement(initial.Request) };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        return controller is null ? await Attempt(lifetime.Token)
            : await controller.RunAsync(operation, Attempt, ApplicationBackgroundOperations.RetireModelsAsync, lifetime.Token);

        void Save(MusicRhymeStep next)
        {
            step = changed(next);
            if (controller?.IsInOperationScope == true) controller.SaveCheckpoint(step);
        }
        async Task<MusicRhymeStep> Attempt(CancellationToken attempt)
        {
            try
            {
                Save(step);
                if (step.ModelReady || step.Stage == "Partial") return step;
                var corpus = await MusicRhymeCorpus.EnsureAsync(progress, attempt);
                step = await MusicRhymeProgramWork.RunAsync(step, next => { Save(next); return step; }, progress, attempt, corpus);
                step = await MusicRhymeCreativeWork.SummarizeAsync(step, models,
                    next => { Save(next); return step; }, progress, attempt);
                if (step.Reference.Length == 0)
                {
                    if (!step.Request.CreativeMode && MusicRhymeTask.ReplyEditableLines(step).Count == 0)
                    { Save(step with { Model = step.Request.Text, ModelReady = true, ModelSkipped = true, Stage = "Ready" }); return step; }
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    progress.Report(new("Reference"));
                    using (var preparation = new RagBookModelPreparation(step.Request.ModelsRoot,
                        new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0))
                        await preparation.PrepareAsync(RagBookModels.EmbeddingId, true,
                            new InlineProgress<ManagedModelDownloadProgress>(_ => progress.Report(new("Verifying"))), attempt);
                    var work = Path.Combine(AppDataPaths.BaseDirectory, "Music", "RhymeQueries", step.Request.Id);
                    Directory.CreateDirectory(work);
                    string books;
                    try
                    {
                        books = await MusicRhymeModelDeadline.RunOperationAsync(cancellation =>
                            RagBookWorker.SearchAsync(corpus, step.Request.ModelsRoot,
                                MusicRhymeTask.SearchQuery(step), progress, cancellation, work,
                                maximumHits: step.Request.Tuning.Integer("RagRawHits")),
                            step.Request.Tuning.Integer("RagTimeoutSeconds"), "Music.Rhyme.Settings.SearchTimedOut", attempt);
                    }
                    finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
                    using var hits = JsonDocument.Parse(books);
                    var selected = hits.RootElement.GetProperty("hits").EnumerateArray().Take(step.Request.Tuning.Integer("RagStoredHits")).Select(h => new {
                        source = h.GetProperty("source").GetString(), text = h.GetProperty("text").GetString()![..Math.Min(step.Request.Tuning.Integer("RagStoredChars"), h.GetProperty("text").GetString()!.Length)] }).ToArray();
                    var lexical = await Task.Run(() => {
                        using var lexicon = new MusicRhymeLexicon(corpus, attempt, tuning: step.Request.Tuning);
                        return lexicon.Reference(Issues(step).Select(i => step.Analysis!.Lines.Single(l => l.Number == i.Line).Ending));
                    }, attempt);
                    using var lexicalDocument = JsonDocument.Parse(lexical);
                    var reference = JsonSerializer.Serialize(new { books = selected, lexical = lexicalDocument.RootElement },
                        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    if (reference.Length > 400000) throw new InvalidDataException("Music.Rhyme.ContextFull");
                    Save(step with { Reference = reference, Stage = "Model" });
                }
                HashSet<int> editable = step.Request.CreativeMode ? [] : MusicRhymeTask.ReplyEditableLines(step);
                if (!step.Request.CreativeMode && editable.Count == 0)
                { Save(step with { Model = step.Request.Text, ModelReady = true, ModelSkipped = true, Stage = "Ready" }); return step; }
                var prompt = step.Reply.Length > 0 && step.Prompt.Length > 0 ? step.Prompt
                    : step.Request.CreativeMode ? MusicRhymeCreativeTask.ComposePrompt(step) : MusicRhymeTask.Prompt(step);
                if (prompt.Length > 400000) throw new InvalidDataException("Music.Rhyme.ContextFull");
                Save(step with { Prompt = prompt, Stage = "Model" }); progress.Report(new("Model"));
                var result = step.Reply;
                if (result.Length == 0)
                {
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    var model = new DebugModelInfo { Name = Path.GetFileName(step.Request.ModelPath!), Path = step.Request.ModelPath!,
                        Format = Path.GetExtension(step.Request.ModelPath!).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? "chatllm" : "gguf" };
                    using var runtime = models();
                    result = step.Request.CreativeMode
                        ? await MusicRhymeModelDeadline.RunOperationAsync(
                            cancellation => runtime.ComposeAsync(model, prompt, step.Request.Tuning, cancellation),
                            step.Request.Tuning.Integer("GenerationTimeoutSeconds"), "Music.Rhyme.Settings.GenerationTimedOut", attempt)
                        : await MusicRhymeModelDeadline.RunAsync(runtime, model, prompt, step.Request.Tuning, attempt);
                    Save(step with { Model = step.Request.CreativeMode ? MusicRhymeCreativeTask.SongFromReply(result) : result, Reply = result });
                }
                if (step.Request.CreativeMode)
                {
                    result = MusicRhymeCreativeTask.SongFromReply(result);
                    MusicRhymeCreativeTask.ValidateSong(result, step.Request.Tuning);
                }
                else MusicRhymeReply.Validate(step.Request.Text, result, editable, step.Request.Tuning);
                var remaining = await Task.Run(() => {
                    using var lexicon = new MusicRhymeLexicon(corpus, attempt, tuning: step.Request.Tuning);
                    var complete = step.Request.Source[..step.Request.Start] + result + step.Request.Source[(step.Request.Start + step.Request.Length)..];
                    var after = MusicRhymeAnalyzer.Analyze(complete, lexicon.Lookup, attempt, step.Request.Start, result.Length, tuning: step.Request.Tuning);
                    // An opportunity always exists, even in good verse. It is not an unresolved fault.
                    return after.Fragments.Any(f => f.Kind == "mismatch");
                }, attempt);
                var unchanged = MusicRhymeReply.Identical(result, step.Request.Text);
                Save(step with { Model = result, ModelReady = true, Stage = remaining || unchanged ? "Partial" : "Ready",
                    Error = unchanged ? "Music.Rhyme.Unchanged" : remaining ? "Music.Rhyme.Unresolved" : null }); return step;
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { Save(step); throw; }
            catch (Exception error)
            { Save(step with { Stage = "Partial", Error = error.Message }); return step; }
        }
    }
    public static IEnumerable<MusicRhymeIssue> Issues(MusicRhymeStep step) => MusicRhymeEditorial.Targets(step);
    public static HashSet<int> EditableLines(MusicRhymeStep step) => MusicRhymeTask.EditableLines(step);
}
