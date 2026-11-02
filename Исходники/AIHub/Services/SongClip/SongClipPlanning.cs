using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>Durable semantic preparation, separate from image/video inference and its immutable checkpoints.</summary>
internal static class SongClipPlanning
{
    internal const int BatchSize = 6, ResponseTokens = 3072;
    private const string Mismatch = "SongClip.VisualPlanMismatch";
    private sealed record Binding(int Schema, string InputHash, string PlanSha256);
    private sealed record Progress(int Schema, string InputHash, SongClipVisualPlan[] Batches);
    private sealed record Input(double Duration, string Hash);

    internal static async Task<SongClipVisualPlan?> EnsureAsync(SongClipAttempt attempt, SongClipStore store, string job,
        Action<double, string> progress, CancellationToken token, SongClipVisualScene[]? slots = null)
    {
        if (attempt.PlanningVersion == 0) return null;
        progress(0, "SongClip.Planning");
        var input = await PrepareInputAsync(attempt, store, token, slots);
        var completed = await ReadAsync(attempt, job, input, token, slots);
        if (completed is not null) { progress(0, "SongClip.PlanReused"); return completed; }
        SongClipPlannerSession? session = null;
        try
        {
            return await EnsureCoreAsync(attempt, job, input.Duration, input.Hash, async (system, body, schema, ct) =>
            {
                session ??= await SongClipPlannerModel.OpenAsync(attempt, ct);
                return await session.GenerateAsync(system, body, schema, ResponseTokens, ct);
            }, progress, token, prepareStory: true, suppliedSlots: slots);
        }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    internal static async Task<SongClipVisualPlan?> ReadCompletedAsync(SongClipAttempt attempt, SongClipStore store,
        string job, CancellationToken token, SongClipVisualScene[]? slots = null)
    {
        if (attempt.PlanningVersion == 0) return null;
        var input = await PrepareInputAsync(attempt, store, token, slots);
        return await ReadAsync(attempt, job, input, token, slots) ?? throw new InvalidDataException(Mismatch);
    }

    private static async Task<Input> PrepareInputAsync(SongClipAttempt attempt, SongClipStore store, CancellationToken token, SongClipVisualScene[]? slots = null)
    {
        if (attempt.PlanningVersion != SongClipVisualProtocol.Version) throw new InvalidDataException(Mismatch);
        var song = store.Resolve(attempt.ProjectId, attempt.Song);
        using var probe = await VideoRuntime.ProbeAsync(song, token);
        if (!double.TryParse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)) throw new InvalidDataException(Mismatch);
        var songHash = await HashFileAsync(song, token);
        var referenceHash = await SongClipReferences.HashAsync(attempt, store, token);
        var hash = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            version = attempt.PlanningVersion, source = SongClipVisualProtocol.InputHash(attempt, duration), songHash, referenceHash,
            planner = SongClipPlannerModel.ModelIdFor(attempt.Model)
        })));
        return new(duration, slots is null ? hash : Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hash, slots }))));
    }

    // Pure orchestration hook: tests exercise cancellation/cache/retry without loading any model or native runtime.
    internal static async Task<SongClipVisualPlan> EnsureCoreAsync(SongClipAttempt attempt, string job, double duration, string boundInput,
        Func<string, string, object, CancellationToken, Task<VideoStructuredAnswer>> generate,
        Action<double, string> report, CancellationToken token, bool prepareStory = false, SongClipVisualScene[]? suppliedSlots = null)
    {
        if (attempt.PlanningVersion != SongClipVisualProtocol.Version) throw new InvalidDataException(Mismatch);
        var input = new Input(duration, boundInput);
        var slots = suppliedSlots ?? SongClipVisualProtocol.Slots(attempt, duration);
        var completed = await ReadAsync(attempt, job, input, token, suppliedSlots);
        if (completed is not null) return completed;
        var reference = await SongClipReferencePlanning.EnsureAsync(attempt, job, boundInput, generate, report, token);
        SongClipStoryPlanning.Prepared? story = null;
        if (prepareStory && SongClipStoryBrief.Lines(attempt).Length > 0)
            story = await SongClipStoryPlanning.EnsureAsync(attempt, job, boundInput, reference, generate, report, token);
        var progressFile = Path.Combine(job, "visual-plan-progress.json");
        var batches = new List<SongClipVisualPlan>();
        if (File.Exists(progressFile))
        {
            var previous = JsonSerializer.Deserialize<Progress>(await File.ReadAllTextAsync(progressFile, token));
            if (previous is null || previous.Schema != 1 || previous.InputHash != boundInput || previous.Batches is null
                || previous.Batches.Length > (slots.Length + BatchSize - 1) / BatchSize) throw new InvalidDataException(Mismatch);
            foreach (var batch in previous.Batches)
            {
                var expected = slots.Skip(batches.Sum(item => item.Scenes.Length)).Take(BatchSize).ToArray();
                _ = Validate(batch, attempt, duration, expected, suppliedSlots);
                if (reference.Model is not null && !batch.Assistants.SequenceEqual([reference.Model]))
                    throw new InvalidDataException(Mismatch);
                batches.Add(FreezeStyleAndModel(batches, batch));
            }
        }
        for (var offset = batches.Sum(batch => batch.Scenes.Length); offset < slots.Length; offset += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            var expected = slots.Skip(offset).Take(BatchSize).ToArray();
            var stage = JsonSerializer.Serialize(new { kind = "planner", stage = "SongClip.PlanningBatch", current = offset / BatchSize + 1,
                total = (slots.Length + BatchSize - 1) / BatchSize, model = SongClipPlannerModel.ModelIdFor(attempt.Model) });
            report(0, stage);
            SongClipVisualPlan? accepted = null;
            // One bounded retry of the same independent schema request. Never render with a malformed/partial response.
            for (var retry = 0; retry < 2; retry++)
            {
                var system = SongClipVisualProtocol.BuildSystem(attempt.Model) + (retry == 0 ? "" :
                    "\nThe previous response failed validation. Follow every supplied index, exact time and required original lyric anchor. Return only the complete JSON schema.");
                try
                {
                    var reply = await generate(system, SongClipVisualProtocol.BuildInput(attempt, duration, expected, batches.FirstOrDefault()?.GlobalStyle,
                        batches.SelectMany(batch => batch.Scenes).TakeLast(2).ToArray(), story?.Anchor ?? "", reference.Roles, suppliedSlots),
                        SongClipVisualProtocol.Schema(expected), token);
                    SongClipStore.Atomic(Path.Combine(job, $"planner-{offset / BatchSize + 1:D3}-{retry + 1}.json"),
                        new { reply.Text, reply.Model });
                    accepted = SongClipVisualProtocol.ParseAndValidate(reply.Text, attempt, duration, [reply.Model], expected, timeline: suppliedSlots);
                    if (story is not null && story.Model != reply.Model) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
                    if (reference.Model is not null && reference.Model != reply.Model) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
                    accepted = FreezeStyleAndModel(batches, accepted); break;
                }
                catch (InvalidDataException error) when (retry == 0 && error.Message is
                    SongClipVisualProtocol.InvalidReply or "VideoPrep.AiInvalidReply" or "VideoPrep.AiIncomplete")
                { report(0, "SongClip.PlanRepairing"); }
            }
            if (accepted is null) throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
            batches.Add(accepted);
            SongClipStore.Atomic(progressFile, new Progress(1, boundInput, batches.ToArray()));
            token.ThrowIfCancellationRequested();
        }
        var plan = new SongClipVisualPlan {
            InputHash = SongClipVisualProtocol.InputHash(attempt, duration),
            GlobalStyle = (story is null ? "" : story.Anchor + "\n") + batches[0].GlobalStyle,
            Scenes = batches.SelectMany(batch => batch.Scenes).ToArray(),
            Assistants = batches.SelectMany(batch => batch.Assistants).Distinct().ToArray(),
            ReferenceRoles = reference.Roles
        };
        _ = Validate(plan, attempt, duration, timeline: suppliedSlots);
        var path = Path.Combine(job, "visual-plan.json"); SongClipStore.Atomic(path, plan);
        SongClipStore.Atomic(Path.Combine(job, "visual-plan-binding.json"), new Binding(1, boundInput, await HashFileAsync(path, token)));
        report(0, "SongClip.PlanReady");
        return plan;
    }

    private static SongClipVisualPlan FreezeStyleAndModel(List<SongClipVisualPlan> previous, SongClipVisualPlan next)
    {
        if (previous.Count == 0) return next;
        // Smart apostrophes are typography, not a changed continuity guide.
        // Keep the first accepted text verbatim; do not relax words, times,
        // anchors or model identity, and never change existing scene prompts.
        static string Apostrophes(string value) => value.Replace('\u2018', '\'').Replace('\u2019', '\'');
        if (Apostrophes(previous[0].GlobalStyle) != Apostrophes(next.GlobalStyle)
            || !previous[0].Assistants.SequenceEqual(next.Assistants))
            throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        return next with { GlobalStyle = previous[0].GlobalStyle };
    }
    private static SongClipVisualPlan Validate(SongClipVisualPlan plan, SongClipAttempt attempt, double duration,
        IReadOnlyList<SongClipVisualScene>? slots = null, IReadOnlyList<SongClipVisualScene>? timeline = null)
    {
        if (plan is null || plan.Version != SongClipVisualProtocol.Version || plan.InputHash != SongClipVisualProtocol.InputHash(attempt, duration))
            throw new InvalidDataException(Mismatch);
        if (slots is null) SongClipReferenceBrief.Validate(plan.ReferenceRoles, attempt);
        return SongClipVisualProtocol.ParseAndValidate(JsonSerializer.Serialize(plan), attempt, duration, plan.Assistants, slots, plan.ReferenceRoles, timeline);
    }
    private static async Task<SongClipVisualPlan?> ReadAsync(SongClipAttempt attempt, string job, Input input, CancellationToken token, SongClipVisualScene[]? slots = null)
    {
        var path = Path.Combine(job, "visual-plan.json"); var receipt = Path.Combine(job, "visual-plan-binding.json");
        if (!File.Exists(receipt)) {
            // Only a pre-render interrupted commit can be rebuilt. Once a worker
            // has started, never assign a newly generated plan to its old result.
            if (new[] { "request.json", "worker-result.json", "motion-state.json", "clip.mp4" }
                .Any(name => File.Exists(Path.Combine(job, name)))) throw new InvalidDataException(Mismatch);
            return null;
        }
        if (!File.Exists(path)) throw new InvalidDataException(Mismatch);
        var binding = JsonSerializer.Deserialize<Binding>(await File.ReadAllTextAsync(receipt, token));
        if (binding is null || binding.Schema != 1 || binding.InputHash != input.Hash
            || binding.PlanSha256 != await HashFileAsync(path, token)) throw new InvalidDataException(Mismatch);
        var plan = JsonSerializer.Deserialize<SongClipVisualPlan>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException(Mismatch);
        SongClipReferenceBrief.Validate(plan.ReferenceRoles, attempt);
        return Validate(plan, attempt, input.Duration, timeline: slots);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
