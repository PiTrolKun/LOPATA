using AIHub.Models;

namespace AIHub.Services;

/// <summary>Cancelable CPU review, independent of model downloads and the application's heavy-operation lease.</summary>
public static class MusicRhymeProgramWork
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<MusicRhymeStep> RunAsync(MusicRhymeStep initial,
        Func<MusicRhymeStep, MusicRhymeStep> changed, IProgress<RagBookProgress> progress,
        CancellationToken token, string? verifiedCorpus = null)
    {
        token.ThrowIfCancellationRequested(); initial.Validate();
        await Gate.WaitAsync(token);
        var step = initial;
        try
        {
            if (step.ModelReady || step.Stage == "Partial") return step;
            var corpus = verifiedCorpus ?? await MusicRhymeCorpus.VerifyInstalledAsync(progress, token);
            if (step.Analysis is null || step.Analysis.SelectionVersion < MusicRhymeSelection.Version)
            {
                progress.Report(new("Analysis"));
                var previous = step.Analysis;
                var analysis = await Task.Run(() => {
                    using var lexicon = new MusicRhymeLexicon(corpus, token, tuning: step.Request.Tuning);
                    return MusicRhymeAnalyzer.Analyze(step.Request.Source, lexicon.Lookup, token,
                        step.Request.Start, step.Request.Length, tuning: step.Request.Tuning);
                }, token);
                token.ThrowIfCancellationRequested();
                step = changed(step with { Analysis = analysis,
                    Suggestions = MusicRhymeEditorial.RebaseSuggestions(previous, analysis, step.Suggestions),
                    Stage = step.ProgramReady ? step.Stage : "Candidates" });
            }
            if (!step.ProgramReady)
            {
                progress.Report(new("Candidates"));
                var program = await Task.Run(() => {
                    using var lexicon = new MusicRhymeLexicon(corpus, token, tuning: step.Request.Tuning);
                    if (MusicRhymeEditorial.ForScope(step, token).Fragments.Length == 0)
                        return new MusicRhymeProgramDraft(step.Request.Text, [], []);
                    var index = MusicRhymeIndex.Ensure(lexicon, progress, token);
                    return MusicRhymeCandidateSearch.Build(index, step, lexicon, token);
                }, token);
                token.ThrowIfCancellationRequested();
                step = changed(step with { Suggestions = program.Candidates, ProgramChanges = program.Changes,
                    Program = program.Text, ProgramDraft = program.Text, ProgramReady = true, Stage = "Reference" });
            }
            return step;
        }
        finally { Gate.Release(); }
    }
}
