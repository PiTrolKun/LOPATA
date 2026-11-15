using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>Checkpointed Any Song director. Uses one approved resident Qwen, then retires it before video inference.</summary>
internal static class SongClipMontagePlanning
{
    private sealed record Binding(int Schema, string InputHash, string PlanSha256);
    private sealed record Progress(int Schema, string InputHash, string ReferenceHash, string StoryHash,
        string BatchesSha256, SongClipMontagePlan[] Batches);

    internal static async Task<SongClipMontagePlan> EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        SongClipMontageAnalysis analysis, Action<double, string> progress, CancellationToken token,
        SongClipPlannerSession? warmedSession = null, string? plannerId = null)
    {
        var boundInput = await PrepareAsync(attempt, store, analysis, token, plannerId);
        var completed = await ReadAsync(attempt, job, analysis, boundInput, token);
        if (completed is not null) { progress(0, "SongClip.PlanReused"); return completed; }
        var session = warmedSession;
        try {
            return await EnsureCoreAsync(attempt, job, analysis, boundInput, async (system, body, schema, ct) => {
                session ??= await SongClipPlannerModel.OpenAsync(attempt, ct, plannerId);
                return await session.GenerateAsync(system, body, schema, SongClipPlannerRecovery.Reserve(body), ct);
            }, progress, token, prepareStory: true, growBatches: true);
        }
        finally { if (session is not null && session != warmedSession) await session.DisposeAsync(); }
    }

    internal static async Task<SongClipMontagePlan> ReadCompletedAsync(SongClipAttempt attempt, SongClipStore store, string job,
        SongClipMontageAnalysis analysis, CancellationToken token, string? plannerId = null)
    {
        var boundInput = await PrepareAsync(attempt, store, analysis, token, plannerId);
        return await ReadAsync(attempt, job, analysis, boundInput, token) ?? throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
    }

    private static async Task<string> PrepareAsync(SongClipAttempt attempt, SongClipStore store,
        SongClipMontageAnalysis analysis, CancellationToken token, string? plannerId)
    {
        token.ThrowIfCancellationRequested(); SongClipMontageProtocol.ValidateInput(attempt, analysis);
        var song = store.Resolve(attempt.ProjectId, attempt.Song);
        using var probe = await VideoRuntime.ProbeAsync(song, token);
        if (!double.TryParse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration)
            || Math.Abs(duration - analysis.DurationSeconds) > .1) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        return SongClipMontageProtocol.Hash(new { schema = 1, source = SongClipMontageProtocol.InputHash(attempt, analysis),
            songHash = await FileHashAsync(song, token), referenceHash = await SongClipReferences.HashAsync(attempt, store, token),
            planner = plannerId ?? SongClipPlannerModel.ModelIdFor(attempt.Model) });
    }

    // The pure hook exercises immutable analysis, repair, checkpoints and resume without a model/runtime.
    internal static async Task<SongClipMontagePlan> EnsureCoreAsync(SongClipAttempt attempt, string job,
        SongClipMontageAnalysis analysis, string boundInput,
        Func<string, string, object, CancellationToken, Task<VideoStructuredAnswer>> generate,
        Action<double, string> report, CancellationToken token, bool prepareStory = false, bool growBatches = false)
    {
        token.ThrowIfCancellationRequested(); SongClipMontageProtocol.ValidateInput(attempt, analysis);
        if (boundInput is null || boundInput.Length != 64 || boundInput.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var completed = await ReadAsync(attempt, job, analysis, boundInput, token);
        if (completed is not null) return completed;
        var reference = await SongClipReferencePlanning.EnsureAsync(attempt, job, boundInput, generate, report, token);
        SongClipStoryPlanning.Prepared? story = null;
        if (prepareStory && SongClipStoryBrief.Lines(attempt).Length > 0)
            story = await SongClipStoryPlanning.EnsureAsync(attempt, job, boundInput, reference, generate, report, token);
        var progressFile = Path.Combine(job, "montage-plan-progress.json");
        var referenceHash = SongClipMontageProtocol.Hash(reference.Roles);
        var storyHash = SongClipMontageProtocol.Hash(story);
        var batches = new List<SongClipMontagePlan>();
        if (File.Exists(progressFile)) {
            var previous = await DeserializeAsync<Progress>(progressFile, token);
            if (previous is null || previous.Schema != 1 || previous.InputHash != boundInput
                || previous.ReferenceHash != referenceHash || previous.StoryHash != storyHash || previous.Batches is null
                || previous.Batches.Length > analysis.Slots.Length
                || previous.BatchesSha256 != SongClipMontageProtocol.Hash(previous.Batches))
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            foreach (var batch in previous.Batches) {
                var offset = batches.Sum(item => item.Shots.Length);
                if (batch?.Shots is not { Length: > 0 } || batch.Shots.Length > analysis.Slots.Length - offset)
                    throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
                var expected = analysis.Slots.AsSpan(offset, batch.Shots.Length).ToArray();
                var validated = SongClipMontageProtocol.Validate(batch, attempt, analysis, expected, batches.LastOrDefault()?.Shots.LastOrDefault());
                RequireModel(reference, story, validated.Assistants[0]);
                if (!EqualsRoles(reference.Roles, validated.ReferenceRoles)) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
                batches.Add(Freeze(batches, validated));
            }
        }
        var preferredCount = batches.LastOrDefault()?.Shots.Length ?? SongClipPlannerRecovery.InitialBatch;
        for (var offset = batches.Sum(batch => batch.Shots.Length); offset < analysis.Slots.Length;) {
            token.ThrowIfCancellationRequested();
            var ordinal = batches.Count + 1;
            report(0, JsonSerializer.Serialize(new { kind = "planner", stage = "SongClip.PlanningBatch", current = offset + 1,
                total = analysis.Slots.Length,
                model = SongClipPlannerModel.ModelIdFor(attempt.Model) }));
            var requestedCount = Math.Min(preferredCount, analysis.Slots.Length - offset);
            var (accepted, count) = await SongClipPlannerRecovery.BatchAsync(requestedCount,
                async (length, pass, guidance) => {
                    var expected = analysis.Slots.AsSpan(offset, length).ToArray();
                    var system = SongClipMontageProtocol.System + (guidance is null ? "" : "\n" + guidance)
                        + (offset == 0 && length == 1 ? "\nWhen a performer reference is provided, the opening shot must show that performer." : "");
                    var response = await generate(system, SongClipMontageProtocol.Input(attempt, analysis, expected,
                        batches.FirstOrDefault()?.GlobalStyle, batches.SelectMany(batch => batch.Shots).TakeLast(2).ToArray(), story?.Anchor ?? "", reference.Roles),
                        SongClipMontageProtocol.Schema(expected.Length, reference.Roles?.Length ?? 0), token);
                    SongClipStore.Atomic(Path.Combine(job, $"montage-planner-{ordinal:D3}-{pass}.json"), new { response.Text, response.Model, response.IsPartial });
                    RequireModel(reference, story, response.Model);
                    return SongClipPlannerRecovery.PreserveValidPrefix(response.Text, "shots", length, (text, available) =>
                        Freeze(batches, SongClipMontageProtocol.Parse(text, attempt, analysis, expected[..available], response.Model,
                            reference.Roles, batches.LastOrDefault()?.Shots.LastOrDefault(), supplyTimedAnchors: true,
                            repairTransitions: guidance is not null)), response.IsPartial);
                }, report, token, plan => plan.Shots.Length);
            batches.Add(accepted);
            offset += count;
            preferredCount = growBatches && count == requestedCount ? count + 1 : count;
            SongClipStore.Atomic(progressFile, new Progress(1, boundInput, referenceHash, storyHash,
                SongClipMontageProtocol.Hash(batches.ToArray()), batches.ToArray()));
            token.ThrowIfCancellationRequested();
        }
        var plan = new SongClipMontagePlan { InputHash = SongClipMontageProtocol.InputHash(attempt, analysis),
            GlobalStyle = (story is null ? "" : story.Anchor + "\n") + batches[0].GlobalStyle,
            Shots = batches.SelectMany(batch => batch.Shots).ToArray(), Assistants = batches[0].Assistants,
            ReferenceRoles = reference.Roles };
        _ = SongClipMontageProtocol.Validate(plan, attempt, analysis);
        var path = Path.Combine(job, "montage-plan.json"); SongClipStore.Atomic(path, plan);
        SongClipStore.Atomic(Path.Combine(job, "montage-plan-binding.json"), new Binding(1, boundInput, await FileHashAsync(path, token)));
        report(0, "SongClip.PlanReady"); return plan;
    }

    private static void RequireModel(SongClipReferencePlanning.Prepared reference, SongClipStoryPlanning.Prepared? story, OutputModelUse model)
    {
        if (!SongClipMontageProtocol.KnownModel(model) || reference.Model is not null && reference.Model != model
            || story is not null && story.Model != model) throw new InvalidDataException(SongClipMontageProtocol.InvalidReply);
    }

    private static SongClipMontagePlan Freeze(List<SongClipMontagePlan> previous, SongClipMontagePlan next)
    {
        if (previous.Count == 0) return next;
        static string Apostrophes(string text) => text.Replace('\u2018', '\'').Replace('\u2019', '\'');
        if (Apostrophes(previous[0].GlobalStyle) != Apostrophes(next.GlobalStyle)
            || !previous[0].Assistants.SequenceEqual(next.Assistants)) throw new InvalidDataException(SongClipMontageProtocol.InvalidReply);
        return next with { GlobalStyle = previous[0].GlobalStyle };
    }

    private static async Task<SongClipMontagePlan?> ReadAsync(SongClipAttempt attempt, string job,
        SongClipMontageAnalysis analysis, string boundInput, CancellationToken token)
    {
        var path = Path.Combine(job, "montage-plan.json"); var bindingFile = Path.Combine(job, "montage-plan-binding.json");
        if (!File.Exists(bindingFile)) {
            if (new[] { "request.json", "worker-result.json", "montage-state.json", "motion-state.json", "clip.mp4" }
                .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            return null;
        }
        if (!File.Exists(path)) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var binding = await DeserializeAsync<Binding>(bindingFile, token);
        if (binding is null || binding.Schema != 1 || binding.InputHash != boundInput || binding.PlanSha256 != await FileHashAsync(path, token))
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var plan = await DeserializeAsync<SongClipMontagePlan>(path, token) ?? throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        return SongClipMontageProtocol.Validate(plan, attempt, analysis);
    }

    private static bool EqualsRoles(SongClipReferenceRole[]? left, SongClipReferenceRole[]? right) => left is null ? right is null
        : right is not null && left.SequenceEqual(right);
    private static async Task<T?> DeserializeAsync<T>(string path, CancellationToken token)
    {
        try { return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token)); }
        catch (JsonException error) { throw new InvalidDataException(SongClipMontageProtocol.Mismatch, error); }
    }
    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
