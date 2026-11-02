using System.IO;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>One durable role translation, reused across scene batches and pause/resume.</summary>
internal static class SongClipReferencePlanning
{
    internal sealed record Prepared(SongClipReferenceRole[]? Roles, OutputModelUse? Model);
    private sealed record Receipt(int Schema, string InputHash, SongClipReferenceRole[] Roles, OutputModelUse Model);

    internal static async Task<Prepared> EnsureAsync(SongClipAttempt attempt, string job, string boundInput,
        Func<string, string, object, CancellationToken, Task<VideoStructuredAnswer>> generate,
        Action<double, string> report, CancellationToken token)
    {
        if (attempt.References is not { Length: > 0 }) return new(null, null);
        SongClipReferences.ValidateNewAttempt(attempt);
        var file = Path.Combine(job, "visual-references.json");
        if (File.Exists(file)) {
            Receipt? previous;
            try { previous = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(file, token)); }
            catch (JsonException error) { throw new InvalidDataException("SongClip.VisualPlanMismatch", error); }
            if (previous is null || previous.Schema != 1 || previous.InputHash != boundInput || previous.Model is null
                || new[] { previous.Model.Id, previous.Model.Name, previous.Model.Revision, previous.Model.Role }.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
            SongClipReferenceBrief.Validate(previous.Roles, attempt);
            return new(previous.Roles, previous.Model);
        }
        report(0, "SongClip.PlanningReferences");
        for (var retry = 0; retry < 2; retry++) {
            token.ThrowIfCancellationRequested();
            try {
                var reply = await generate(SongClipReferenceBrief.System + (retry == 0 ? "" :
                    "\nThe previous answer failed validation. Keep every index and kind exactly, and translate each supplied role into a complete English sentence. Return only the complete JSON schema."),
                    SongClipReferenceBrief.Input(attempt), SongClipReferenceBrief.Schema(attempt.References.Length), token);
                SongClipStore.Atomic(Path.Combine(job, $"planner-references-{retry + 1}.json"), new { reply.Text, reply.Model });
                var roles = SongClipReferenceBrief.Parse(reply.Text, attempt);
                if (reply.Model is null || new[] { reply.Model.Id, reply.Model.Name, reply.Model.Revision, reply.Model.Role }.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
                SongClipStore.Atomic(file, new Receipt(1, boundInput, roles, reply.Model));
                token.ThrowIfCancellationRequested(); return new(roles, reply.Model);
            }
            catch (InvalidDataException error) when (retry == 0 && error.Message is
                SongClipVisualProtocol.InvalidReply or "VideoPrep.AiInvalidReply" or "VideoPrep.AiIncomplete")
            { report(0, "SongClip.PlanRepairing"); }
        }
        throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
    }
}
