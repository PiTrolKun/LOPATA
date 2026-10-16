using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Checkpointed first stage; a resumed composition never repeats an already completed summary.</summary>
internal static class MusicRhymeCreativeWork
{
    internal static async Task<MusicRhymeStep> SummarizeAsync(MusicRhymeStep step,
        Func<IMusicRhymeModel> models, Func<MusicRhymeStep, MusicRhymeStep> save,
        IProgress<RagBookProgress> progress, CancellationToken token)
    {
        if (!step.Request.CreativeMode || step.CreativeSummary.Length > 0) return step;
        var prompt = MusicRhymeCreativeTask.SummaryPrompt(step);
        if (prompt.Length > 400_000) throw new InvalidDataException("Music.Rhyme.ContextFull");
        step = save(step with { CreativeSummaryPrompt = prompt, Stage = "Summary" });
        progress.Report(new("Summary"));
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var model = new DebugModelInfo { Name = Path.GetFileName(step.Request.ModelPath!), Path = step.Request.ModelPath!,
            Format = Path.GetExtension(step.Request.ModelPath!).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? "chatllm" : "gguf" };
        using var runtime = models();
        var summary = await MusicRhymeModelDeadline.RunOperationAsync(
            cancellation => runtime.SummarizeAsync(model, prompt, step.Request.Tuning, cancellation),
            step.Request.Tuning.Integer("GenerationTimeoutSeconds"), "Music.Rhyme.Settings.GenerationTimedOut", token);
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > step.Request.Tuning.Integer("MaxAnswerChars"))
            throw new InvalidDataException("Music.Rhyme.InvalidReply");
        return save(step with { CreativeSummary = summary, Stage = "Reference" });
    }
}
