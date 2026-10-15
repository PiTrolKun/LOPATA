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
        initial.Validate(); var step = initial;
        var controller = ApplicationBackgroundOperations.Current;
        var operation = restored ?? new BackgroundOperationState { Kind = Kind, Title = title ?? "Rhyme workshop",
            Project = initial.Request.ProjectId, Private = true, Input = JsonSerializer.SerializeToElement(initial.Request) };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        return controller is null ? await Attempt(token)
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
                if (step.Analysis is null)
                {
                    var analysis = await Task.Run(() => {
                        using var lexicon = new MusicRhymeLexicon(corpus, attempt);
                        return MusicRhymeAnalyzer.Analyze(step.Request.Source, lexicon.Lookup, attempt);
                    }, attempt);
                    Save(step with { Analysis = analysis, Stage = "Candidates" });
                }
                if (!step.ProgramReady)
                {
                    var program = await Task.Run(() => {
                        using var lexicon = new MusicRhymeLexicon(corpus, attempt);
                        var issues = Issues(step).Where(i => i.Kind == "Rhyme").ToArray();
                        if (issues.Length == 0) return (Suggestions: Array.Empty<MusicRhymeSuggestion>(), Text: step.Request.Text);
                        var index = MusicRhymeIndex.Ensure(lexicon, progress, attempt);
                        var suggestions = issues.SelectMany(i => MusicRhymeIndex.Find(index, step.Analysis!.Lines.Single(l => l.Number == i.Line),
                            step.Analysis!.Lines.Single(l => l.Number == i.Partner),
                            lexicon.Synonyms(step.Analysis!.Lines.Single(l => l.Number == i.Line).Ending), attempt,
                            word => lexicon.Lookup(word).Any(h => h.Source.StartsWith("openrussian-", StringComparison.Ordinal)
                                || h.Source.StartsWith("english-wordnet-", StringComparison.Ordinal)))).ToArray();
                        return (Suggestions: suggestions, Text: MusicRhymeProgram.Build(step, suggestions, lexicon.Lookup));
                    }, attempt);
                    Save(step with { Suggestions = program.Suggestions, Program = program.Text, ProgramDraft = program.Text, ProgramReady = true, Stage = "Reference" });
                }
                if (step.Reference.Length == 0)
                {
                    if (EditableLines(step).Count == 0)
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
                        books = await RagBookWorker.SearchAsync(corpus, step.Request.ModelsRoot,
                            MusicRhymeTask.SearchQuery(step), progress, attempt, work);
                    }
                    finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
                    using var hits = JsonDocument.Parse(books);
                    var selected = hits.RootElement.GetProperty("hits").EnumerateArray().Take(2).Select(h => new {
                        source = h.GetProperty("source").GetString(), text = h.GetProperty("text").GetString()![..Math.Min(900, h.GetProperty("text").GetString()!.Length)] }).ToArray();
                    var lexical = await Task.Run(() => {
                        using var lexicon = new MusicRhymeLexicon(corpus, attempt);
                        return lexicon.Reference(Issues(step).Select(i => step.Analysis!.Lines.Single(l => l.Number == i.Line).Ending));
                    }, attempt);
                    using var lexicalDocument = JsonDocument.Parse(lexical);
                    Save(step with { Reference = JsonSerializer.Serialize(new { books = selected, lexical = lexicalDocument.RootElement }), Stage = "Model" });
                }
                var editable = EditableLines(step);
                if (editable.Count == 0)
                { Save(step with { Model = step.Request.Text, ModelReady = true, ModelSkipped = true, Stage = "Ready" }); return step; }
                var prompt = MusicRhymeTask.Prompt(step);
                Save(step with { Prompt = prompt, Stage = "Model" }); progress.Report(new("Model"));
                var result = step.Reply;
                if (result.Length == 0)
                {
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    var model = new DebugModelInfo { Name = Path.GetFileName(step.Request.ModelPath!), Path = step.Request.ModelPath!,
                        Format = Path.GetExtension(step.Request.ModelPath!).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? "chatllm" : "gguf" };
                    using var runtime = models();
                    result = await runtime.RewriteAsync(model, prompt, attempt);
                    Save(step with { Model = result, Reply = result });
                }
                MusicRhymeReply.Validate(step.Request.Text, result, editable);
                var remaining = await Task.Run(() => {
                    using var lexicon = new MusicRhymeLexicon(corpus, attempt);
                    var complete = step.Request.Source[..step.Request.Start] + result + step.Request.Source[(step.Request.Start + step.Request.Length)..];
                    var after = MusicRhymeAnalyzer.Analyze(complete, lexicon.Lookup, attempt);
                    return after.Issues.Any(i => i.Kind is "Rhyme" or "Rhythm" && after.Lines.Any(l => l.Number == i.Line
                        && l.Start >= step.Request.Start && l.Start < step.Request.Start + result.Length));
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
    public static IEnumerable<MusicRhymeIssue> Issues(MusicRhymeStep step) => step.Analysis!.Issues.Where(i => {
        var line = step.Analysis.Lines.Single(l => l.Number == i.Line);
        return line.Start >= step.Request.Start && line.Start < step.Request.Start + step.Request.Length;
    });
    public static HashSet<int> EditableLines(MusicRhymeStep step) => MusicRhymeTask.EditableLines(step);
}
