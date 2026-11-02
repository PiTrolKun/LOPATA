using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipPlanningTests
{
    private const string Style = "A coherent cinematic workshop with the same ginger cat, brass clocks and warm window light.";
    private static readonly OutputModelUse Assistant = new("test-qwen", "Test Qwen", "revision-a", "song-to-clip semantic planning");

    [TestMethod]
    public async Task ThirtyFiveScenesUseSixIndependentBatchesAndCompletedPlanReusesWithoutModelCalls()
    {
        using var fixture = new Fixture(280); var indices = new List<int[]>(); var styles = new List<string?>();
        var plan = await fixture.RunAsync((system, body, schema, token) => {
            using var input = JsonDocument.Parse(body); using var contract = JsonDocument.Parse(JsonSerializer.Serialize(schema));
            var slots = input.RootElement.GetProperty("slots");
            indices.Add(slots.EnumerateArray().Select(row => row.GetProperty("index").GetInt32()).ToArray());
            styles.Add(input.RootElement.GetProperty("acceptedGlobalStyle").GetString());
            var rows = contract.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("scenes");
            Assert.AreEqual(slots.GetArrayLength(), rows.GetProperty("minItems").GetInt32());
            Assert.AreEqual(slots.GetArrayLength(), rows.GetProperty("maxItems").GetInt32());
            Assert.Contains("untrusted artistic source DATA", system);
            return Task.FromResult(Reply(body));
        });
        Assert.HasCount(6, indices); Assert.HasCount(35, plan.Scenes); Assert.HasCount(1, plan.Assistants);
        CollectionAssert.AreEqual(Enumerable.Range(1, 35).ToArray(), indices.SelectMany(batch => batch).ToArray());
        CollectionAssert.AreEqual(new[] { 6, 6, 6, 6, 6, 5 }, indices.Select(batch => batch.Length).ToArray());
        Assert.IsNull(styles[0]); Assert.IsTrue(styles.Skip(1).All(style => style == Style));
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(fixture.Progress));
        Assert.AreEqual(6, checkpoint.RootElement.GetProperty("Batches").GetArrayLength());
        var before = File.ReadAllBytes(fixture.Plan); var binding = File.ReadAllBytes(fixture.Binding);
        var reused = await fixture.RunAsync((_, _, _, _) => throw new AssertFailedException("A validated complete plan must not load a model."));
        Assert.AreEqual(plan.InputHash, reused.InputHash);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Plan)); CollectionAssert.AreEqual(binding, File.ReadAllBytes(fixture.Binding));
    }

    [TestMethod]
    public async Task CancellationKeepsAcceptedBatchAndResumeStartsAtTheNextGlobalScene()
    {
        using var fixture = new Fixture(104); using var cancel = new CancellationTokenSource(); var generated = new List<int>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync((_, body, _, token) => {
            token.ThrowIfCancellationRequested(); generated.Add(FirstIndex(body)); return Task.FromResult(Reply(body));
        }, cancel.Token, report: (_, stage) => {
            if (stage.StartsWith('{')) {
                using var status = JsonDocument.Parse(stage);
                if (status.RootElement.GetProperty("current").GetInt32() == 2) cancel.Cancel();
            }
        }));
        CollectionAssert.AreEqual(new[] { 1 }, generated.ToArray());
        Assert.IsFalse(File.Exists(fixture.Plan)); Assert.IsFalse(File.Exists(fixture.Binding));
        using (var partial = JsonDocument.Parse(File.ReadAllText(fixture.Progress)))
            Assert.AreEqual(1, partial.RootElement.GetProperty("Batches").GetArrayLength());
        var firstResponse = File.ReadAllBytes(Path.Combine(fixture.Job, "planner-001-1.json")); generated.Clear();
        var complete = await fixture.RunAsync((_, body, _, _) => {
            generated.Add(FirstIndex(body)); return Task.FromResult(Reply(body));
        });
        CollectionAssert.AreEqual(new[] { 7, 13 }, generated.ToArray()); Assert.HasCount(13, complete.Scenes);
        CollectionAssert.AreEqual(firstResponse, File.ReadAllBytes(Path.Combine(fixture.Job, "planner-001-1.json")));
    }

    [TestMethod]
    public async Task PreCanceledRequestCreatesNoPlanOrPlannerReceipts()
    {
        using var fixture = new Fixture(24); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync((_, _, _, _) =>
            throw new AssertFailedException("Canceled planning must not enter the model."), cancel.Token));
        Assert.HasCount(0, Directory.GetFiles(fixture.Job));
    }

    [TestMethod]
    [DataRow("bound-hash")]
    [DataRow("plan-hash")]
    [DataRow("missing-plan")]
    [DataRow("schema")]
    [DataRow("lyrics")]
    [DataRow("generator")]
    [DataRow("duration")]
    public async Task CompletedCacheRejectsChangedBindingOrSemanticInputsBeforeCallingModel(string problem)
    {
        using var fixture = new Fixture(24);
        await fixture.RunAsync((_, body, _, _) => Task.FromResult(Reply(body)));
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.Binding))!;
        if (problem == "plan-hash") File.AppendAllText(fixture.Plan, " ");
        if (problem == "missing-plan") File.Delete(fixture.Plan);
        if (problem == "schema") { receipt["Schema"] = 2; File.WriteAllText(fixture.Binding, receipt.ToJsonString()); }
        var attempt = problem == "lyrics" ? fixture.Attempt with { Lyrics = "A different song about an empty harbor." }
            : problem == "generator" ? fixture.Attempt with { Model = "h3_music_context" } : fixture.Attempt;
        var before = File.ReadAllBytes(fixture.Binding);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync((_, _, _, _) =>
            throw new AssertFailedException("An invalid cached plan must be rejected before generation."),
            boundInput: problem == "bound-hash" ? new('b', 64) : null,
            attempt: attempt, duration: problem == "duration" ? 25 : null));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding));
    }

    [TestMethod]
    [DataRow("raw-json")]
    [DataRow("VideoPrep.AiInvalidReply")]
    [DataRow("VideoPrep.AiIncomplete")]
    public async Task KnownReplyFailureGetsOneBoundedRepair(string failure)
    {
        using var fixture = new Fixture(24); var calls = 0; var repairs = 0;
        var result = await fixture.RunAsync((system, body, _, _) => {
            calls++;
            if (calls == 1) {
                if (failure == "raw-json") return Task.FromResult(new VideoStructuredAnswer("{}", Assistant));
                throw new InvalidDataException(failure);
            }
            Assert.Contains("previous response failed validation", system);
            return Task.FromResult(Reply(body));
        }, report: (_, stage) => { if (stage == "SongClip.PlanRepairing") repairs++; });
        Assert.AreEqual(2, calls); Assert.AreEqual(1, repairs); Assert.HasCount(3, result.Scenes);
    }

    [TestMethod]
    public async Task RepeatedInvalidReplyStopsAfterTwoCallsWithoutCommittingPartialPlan()
    {
        using var fixture = new Fixture(24); var calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync((_, _, _, _) => {
            calls++; return Task.FromResult(new VideoStructuredAnswer("{}", Assistant));
        }));
        Assert.AreEqual(2, calls); Assert.IsFalse(File.Exists(fixture.Progress));
        Assert.IsFalse(File.Exists(fixture.Plan)); Assert.IsFalse(File.Exists(fixture.Binding));
    }

    [TestMethod]
    [DataRow("SongClip.PlannerModelMissing")]
    [DataRow("VideoPrep.AiModelUnverified")]
    [DataRow("VideoPrep.GpuNeedsDecision")]
    public async Task NonReplyFailureDoesNotEnterTheRepairLoop(string failure)
    {
        using var fixture = new Fixture(24); var calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync((_, _, _, _) => {
            calls++; throw new InvalidDataException(failure);
        }));
        Assert.AreEqual(1, calls); Assert.HasCount(0, Directory.GetFiles(fixture.Job));
    }

    [TestMethod]
    public async Task SmartApostrophesKeepFirstStyleAndResumeAtTheNextBatch()
    {
        using var fixture = new Fixture(104);
        const string original = "The cat's fur and the room's warm window light remain consistent across the cinematic workshop.";
        var calls = new List<int>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync((_, body, _, _) => {
            var index = FirstIndex(body); calls.Add(index);
            if (index == 13) throw new OperationCanceledException();
            return Task.FromResult(Reply(body, style: index == 1 ? original : original.Replace('\'', '\u2019')));
        }));
        CollectionAssert.AreEqual(new[] { 1, 7, 13 }, calls.ToArray());
        var firstReceipt = File.ReadAllBytes(Path.Combine(fixture.Job, "planner-001-1.json"));
        var secondReceipt = File.ReadAllBytes(Path.Combine(fixture.Job, "planner-002-1.json"));
        using (var partial = JsonDocument.Parse(File.ReadAllText(fixture.Progress))) {
            var batches = partial.RootElement.GetProperty("Batches");
            Assert.AreEqual(2, batches.GetArrayLength());
            Assert.IsTrue(batches.EnumerateArray().All(batch => batch.GetProperty("globalStyle").GetString() == original));
        }
        calls.Clear();
        var plan = await fixture.RunAsync((_, body, _, _) => {
            calls.Add(FirstIndex(body));
            return Task.FromResult(Reply(body, style: original.Replace('\'', '\u2018')));
        });
        CollectionAssert.AreEqual(new[] { 13 }, calls.ToArray());
        Assert.AreEqual(original, plan.GlobalStyle);
        CollectionAssert.AreEqual(firstReceipt, File.ReadAllBytes(Path.Combine(fixture.Job, "planner-001-1.json")));
        CollectionAssert.AreEqual(secondReceipt, File.ReadAllBytes(Path.Combine(fixture.Job, "planner-002-1.json")));
    }

    [TestMethod]
    [DataRow("style")]
    [DataRow("model")]
    public async Task LaterBatchCannotChangeGlobalStyleOrRecordedModel(string changed)
    {
        using var fixture = new Fixture(104); var calls = new List<int>();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync((_, body, _, _) => {
            var index = FirstIndex(body); calls.Add(index);
            return Task.FromResult(Reply(body,
                style: changed == "style" && index > 1 ? "A different cinematic world with icy neon towers and cold blue sunlight." : Style,
                model: changed == "model" && index > 1 ? Assistant with { Revision = "revision-b" } : Assistant));
        }));
        CollectionAssert.AreEqual(new[] { 1, 7, 7 }, calls.ToArray());
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(fixture.Progress));
        Assert.AreEqual(1, checkpoint.RootElement.GetProperty("Batches").GetArrayLength());
        Assert.IsFalse(File.Exists(fixture.Plan)); Assert.IsFalse(File.Exists(fixture.Binding));
        var complete = await fixture.RunAsync((_, body, _, _) => Task.FromResult(Reply(body)));
        Assert.HasCount(13, complete.Scenes); Assert.AreEqual(Assistant, complete.Assistants.Single());
    }

    [TestMethod]
    public async Task PartialCheckpointWithDifferentBoundInputIsRejectedWithoutOverwritingIt()
    {
        using var fixture = new Fixture(104); var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync((_, body, _, _) => {
            if (++calls == 2) throw new OperationCanceledException();
            return Task.FromResult(Reply(body));
        }));
        var before = File.ReadAllBytes(fixture.Progress);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync((_, _, _, _) =>
            throw new AssertFailedException("Mismatched partial input must not call the model."), boundInput: new('c', 64)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Progress));
    }

    [TestMethod]
    [DataRow("glitchframe", "core")]
    [DataRow("h3_music_context", "executor")]
    [DataRow("geekatplay_music_video", "executor")]
    [DataRow("h3_any_song", "executor")]
    [DataRow("infinitetalk", "executor")]
    [DataRow("longcat_avatar", "executor")]
    [DataRow("future-heavy-pipeline", "executor")]
    public void ModelPolicyIsExactAndFuturePipelinesUseExecutor(string model, string planner) =>
        Assert.AreEqual(planner, SongClipPlannerModel.ModelIdFor(model));

    [TestMethod]
    [DataRow("request.json")]
    [DataRow("worker-result.json")]
    [DataRow("motion-state.json")]
    [DataRow("clip.mp4")]
    public async Task MissingPlanBindingNeverReplansAnAttemptWithExistingWorkerMaterial(string name)
    {
        using var fixture = new Fixture(20); var file = Path.Combine(fixture.Job, name);
        File.WriteAllText(file, "preserved worker material");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync((_, _, _, _) =>
            throw new AssertFailedException("Never invent a new plan for an existing worker result.")));
        Assert.AreEqual("preserved worker material", File.ReadAllText(file));
        Assert.IsFalse(File.Exists(fixture.Plan));
    }

    private static int FirstIndex(string body)
    {
        using var input = JsonDocument.Parse(body); return input.RootElement.GetProperty("slots")[0].GetProperty("index").GetInt32();
    }
    private static VideoStructuredAnswer Reply(string body, string style = Style, OutputModelUse? model = null)
    {
        using var input = JsonDocument.Parse(body);
        var source = input.RootElement.GetProperty("lyricsLines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        var timed = input.RootElement.GetProperty("knownTimingCues").GetArrayLength() != 0;
        var scenes = input.RootElement.GetProperty("slots").EnumerateArray().Select(slot => {
            var index = slot.GetProperty("index").GetInt32();
            var anchors = slot.GetProperty("lyricAnchors").EnumerateArray().Select(row => row.GetString()!).ToArray();
            if (!timed && anchors.Length == 0 && source.Length > 0) anchors = [source[0]];
            return new { index, startSeconds = slot.GetProperty("startSeconds").GetDouble(), endSeconds = slot.GetProperty("endSeconds").GetDouble(),
                prompt = $"At moment {index}, a ginger cat adjusts a brass clock beside the workshop window under warm light.", lyricAnchors = anchors };
        }).ToArray();
        return new(JsonSerializer.Serialize(new { globalStyle = style, scenes }), model ?? Assistant);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicProjectTests.Files _files = new();
        private readonly double _duration;
        internal SongClipAttempt Attempt { get; }
        internal string Job { get; }
        internal string Progress => Path.Combine(Job, "visual-plan-progress.json");
        internal string Plan => Path.Combine(Job, "visual-plan.json");
        internal string Binding => Path.Combine(Job, "visual-plan-binding.json");
        internal Fixture(double duration)
        {
            _duration = duration; Job = Path.Combine(_files.Root, "attempt"); Directory.CreateDirectory(Job);
            Attempt = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "attachments/song.wav", "", "Planner fixture",
                "Кот чинит старые часы.\nПотом выходит встречать рассвет.", "", new() { Interval = 8 }, _files.Root, 2)
                { PlanningVersion = SongClipVisualProtocol.Version };
        }
        internal Task<SongClipVisualPlan> RunAsync(Func<string, string, object, CancellationToken, Task<VideoStructuredAnswer>> generate,
            CancellationToken token = default, string? boundInput = null, SongClipAttempt? attempt = null, double? duration = null,
            Action<double, string>? report = null) => SongClipPlanning.EnsureCoreAsync(attempt ?? Attempt, Job, duration ?? _duration,
                boundInput ?? new('a', 64), generate, report ?? ((_, _) => { }), token);
        public void Dispose() => _files.Dispose();
    }
}
