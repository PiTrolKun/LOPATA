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
        var roles = new List<SongClipReferenceRole>();
        OutputModelUse? model = null;
        var checkpointFile = Path.Combine(job, "visual-references-progress.json");
        if (File.Exists(checkpointFile))
        {
            var checkpoint = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(checkpointFile, token));
            if (checkpoint is not { Schema: 1, Roles.Length: > 0 } || checkpoint.InputHash != boundInput
                || !SongClipMontageProtocol.KnownModel(checkpoint.Model))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
            SongClipReferenceBrief.ValidateRange(checkpoint.Roles, attempt.References, 0, checkpoint.Roles.Length);
            roles.AddRange(checkpoint.Roles);
            model = checkpoint.Model;
        }
        var preferredCount = Math.Min(attempt.References.Length, SongClipReferences.MaximumCount);
        while (roles.Count < attempt.References.Length)
        {
            var start = roles.Count;
            var (result, count) = await SongClipPlannerRecovery.BatchAsync(Math.Min(preferredCount, attempt.References.Length - start),
                async (length, pass, guidance) => {
                    var system = SongClipReferenceBrief.System + (guidance is null ? "" :
                        "\nKeep every supplied index and kind exactly and translate each role into a complete English sentence. Return complete JSON.");
                    var reply = await generate(system, SongClipReferenceBrief.Input(attempt, start, length),
                        SongClipReferenceBrief.Schema(length, start), token);
                    var name = start == 0 ? $"planner-references-{pass}.json" : $"planner-references-{start + 1:D3}-{pass}.json";
                    SongClipStore.Atomic(Path.Combine(job, name), new { reply.Text, reply.Model, reply.IsPartial });
                    if (reply.IsPartial) throw new InvalidDataException("VideoPrep.AiIncomplete");
                    if (!SongClipMontageProtocol.KnownModel(reply.Model) || model is not null && model != reply.Model)
                        throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
                    return (Roles: SongClipReferenceBrief.Parse(reply.Text, attempt, start, length), reply.Model);
                }, report, token);
            roles.AddRange(result.Roles);
            model = result.Model;
            preferredCount = count;
            SongClipStore.Atomic(checkpointFile, new Receipt(1, boundInput, roles.ToArray(), model));
        }
        var completed = roles.ToArray();
        SongClipStore.Atomic(file, new Receipt(1, boundInput, completed, model!));
        token.ThrowIfCancellationRequested();
        return new(completed, model);
    }
}
