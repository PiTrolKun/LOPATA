using System.IO;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>Durable literal lyric extraction with bounded malformed-answer repair.</summary>
internal static class SongClipStoryPlanning
{
    internal sealed record Prepared(int Schema, string InputHash, string Anchor, OutputModelUse Model);

    internal static async Task<Prepared> EnsureAsync(SongClipAttempt attempt, string job, string boundInput,
        SongClipReferencePlanning.Prepared reference,
        Func<string, string, object, CancellationToken, Task<VideoStructuredAnswer>> generate,
        Action<double, string> report, CancellationToken token)
    {
        var file = Path.Combine(job, "visual-story.json");
        if (File.Exists(file)) {
            Prepared? saved;
            try { saved = JsonSerializer.Deserialize<Prepared>(await File.ReadAllTextAsync(file, token)); }
            catch (JsonException error) { throw new InvalidDataException("SongClip.VisualPlanMismatch", error); }
            if (saved is null || saved.Schema != 1 || saved.InputHash != boundInput || !KnownModel(saved.Model)
                || reference.Model is not null && saved.Model != reference.Model)
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
            SongClipVisualProtocol.ValidateAnchor(saved.Anchor); return saved;
        }
        report(0, "SongClip.Interpreting");
        for (var retry = 0; retry < 2; retry++) {
            token.ThrowIfCancellationRequested();
            try {
                var response = await generate(SongClipStoryBrief.SystemFor(reference.Roles) + (retry == 0 ? "" :
                    "\nThe previous response failed validation. Choose each sourceLine verbatim from the supplied enum; do not shorten, translate or paraphrase it. Each visual must be a concise English image of 2 to 80 characters. Return only the complete JSON schema."),
                    SongClipStoryBrief.Input(attempt, reference.Roles), SongClipStoryBrief.Schema(attempt), token);
                // Retain the actual rejected answer as evidence; never silently repair its source text.
                SongClipStore.Atomic(Path.Combine(job, $"planner-story-{retry + 1}.json"), new { response.Text, response.Model });
                var anchor = SongClipStoryBrief.Parse(response.Text, attempt);
                if (!KnownModel(response.Model) || reference.Model is not null && response.Model != reference.Model)
                    throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
                var story = new Prepared(1, boundInput, anchor, response.Model);
                SongClipStore.Atomic(file, story); token.ThrowIfCancellationRequested(); return story;
            }
            catch (InvalidDataException error) when (retry == 0 && error.Message is
                SongClipVisualProtocol.InvalidReply or "VideoPrep.AiInvalidReply" or "VideoPrep.AiIncomplete")
            { report(0, "SongClip.PlanRepairing"); }
        }
        throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
    }

    private static bool KnownModel(OutputModelUse? model) => model is not null
        && !new[] { model.Id, model.Name, model.Revision, model.Role }.Any(string.IsNullOrWhiteSpace);
}
