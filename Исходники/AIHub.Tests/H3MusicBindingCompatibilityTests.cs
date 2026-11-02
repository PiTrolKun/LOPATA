using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class H3MusicBindingCompatibilityTests
{
    private const string Worker = "AIHub.SongClip.MotionWorker";
    private const string LegacyWorker = "5A75C63AA6F1A947689F995E8C5BAA5185DC591303F9059024C4B26ADF384CFE";
    private const string SingleReferenceWorker = "685A48AD75E18C61B337D244054BD9525C7A1A2D1C435ED8CD473C50753ED7B5";

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task ExactSingleReferenceAdapterKeepsHistoricalPlansAndCheckpointBinding(int planningVersion)
    {
        using var fixture = new Fixture();
        fixture.Attempt = fixture.Attempt with { PlanningVersion = planningVersion };
        var adapters = CurrentAdapters(); adapters[Worker] = SingleReferenceWorker;
        fixture.WriteLegacyBinding(adapters); var before = File.ReadAllBytes(fixture.Binding);
        await fixture.EnsureAsync();
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding)); fixture.AssertArtifactsUnchanged();
        using var receipt = JsonDocument.Parse(File.ReadAllText(fixture.Compatibility));
        Assert.AreEqual("known-single-reference-h3-motion-worker", receipt.RootElement.GetProperty("Policy").GetString());
        Assert.AreEqual(planningVersion, receipt.RootElement.GetProperty("PlanningVersion").GetInt32());
    }

    [TestMethod]
    public async Task PreviousCompatibilityReceiptIsPreservedAndGetsAnExplicitCurrentLink()
    {
        using var fixture = new Fixture();
        var previous = CurrentAdapters(); previous[Worker] = SingleReferenceWorker;
        fixture.WriteLegacyBinding(previous); var priorCurrentHash = fixture.LegacyHash;
        fixture.WriteLegacyBinding(); var legacyHash = fixture.LegacyHash;
        var legacy = CurrentAdapters(); legacy[Worker] = LegacyWorker;
        SongClipStore.Atomic(fixture.Compatibility, new { Schema = 1, Policy = "known-pre-semantic-h3-motion-worker", PlanningVersion = 0,
            LegacyInputHash = legacyHash, CurrentInputHash = priorCurrentHash, LegacyAdapters = legacy, CurrentAdapters = previous });
        var before = File.ReadAllBytes(fixture.Compatibility);
        await fixture.EnsureAsync();
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Compatibility)); fixture.AssertArtifactsUnchanged();
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Binding)!, "input-binding-reference-compatibility.json")));
    }

    [TestMethod]
    [DataRow("second-image")]
    [DataRow("kind")]
    [DataRow("description")]
    [DataRow("order")]
    public async Task EveryRoleAwareReferenceIsBoundBeforeAnyCompletedResultCanBeReused(string change)
    {
        using var fixture = new Fixture();
        var second = "attachments/place.png";
        File.WriteAllBytes(fixture.Store.Resolve(fixture.Attempt.ProjectId, second), [10, 11, 12]);
        fixture.Attempt = fixture.Attempt with { PlanningVersion = 1, References = [
            new(fixture.Attempt.Reference, "performer", "The singer performs the song."), new(second, "world", "The song events happen here.")] };
        // The fixture owns these small fake results; this case starts before any result exists.
        File.Delete(Path.Combine(Path.GetDirectoryName(fixture.Binding)!, "clip.mp4"));
        File.Delete(Path.Combine(Path.GetDirectoryName(fixture.Binding)!, "worker-result.json"));
        await fixture.EnsureAsync(); var before = File.ReadAllBytes(fixture.Binding);
        if (change == "second-image") File.AppendAllText(fixture.Store.Resolve(fixture.Attempt.ProjectId, second), "changed");
        if (change == "kind") fixture.Attempt = fixture.Attempt with { References = [fixture.Attempt.References[0], fixture.Attempt.References[1] with { Kind = "object" }] };
        if (change == "description") fixture.Attempt = fixture.Attempt with { References = [fixture.Attempt.References[0], fixture.Attempt.References[1] with { Description = "A different role in the clip." }] };
        if (change == "order") fixture.Attempt = fixture.Attempt with { Reference = second, References = fixture.Attempt.References.Reverse().ToArray() };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(fixture.EnsureAsync);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding));
        Assert.IsFalse(File.Exists(fixture.Compatibility));
    }

    [TestMethod]
    public async Task KnownLegacyWorkerIsAcceptedWithExplicitEvidenceAndWithoutRebindingOldWork()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(JsonSerializer.SerializeToElement(fixture.Attempt).TryGetProperty("PlanningVersion", out _),
            "Zero planning version must preserve the exact original attempt JSON identity.");
        fixture.WriteLegacyBinding();
        var binding = File.ReadAllBytes(fixture.Binding);
        await fixture.EnsureAsync();
        CollectionAssert.AreEqual(binding, File.ReadAllBytes(fixture.Binding)); fixture.AssertArtifactsUnchanged();
        using var evidence = JsonDocument.Parse(File.ReadAllText(fixture.Compatibility));
        var receipt = evidence.RootElement;
        Assert.AreEqual(1, receipt.GetProperty("Schema").GetInt32());
        Assert.AreEqual("known-pre-semantic-h3-motion-worker", receipt.GetProperty("Policy").GetString());
        Assert.AreEqual(0, receipt.GetProperty("PlanningVersion").GetInt32());
        Assert.AreEqual(fixture.LegacyHash, receipt.GetProperty("LegacyInputHash").GetString());
        Assert.AreNotEqual(fixture.LegacyHash, receipt.GetProperty("CurrentInputHash").GetString());
        Assert.AreEqual(LegacyWorker, receipt.GetProperty("LegacyAdapters").GetProperty(Worker).GetString());
        foreach (var adapter in CurrentAdapters())
        {
            Assert.AreEqual(adapter.Value, receipt.GetProperty("CurrentAdapters").GetProperty(adapter.Key).GetString());
            if (adapter.Key != Worker) Assert.AreEqual(adapter.Value, receipt.GetProperty("LegacyAdapters").GetProperty(adapter.Key).GetString());
        }
        var evidenceBefore = File.ReadAllBytes(fixture.Compatibility);
        await fixture.EnsureAsync();
        CollectionAssert.AreEqual(binding, File.ReadAllBytes(fixture.Binding));
        CollectionAssert.AreEqual(evidenceBefore, File.ReadAllBytes(fixture.Compatibility)); fixture.AssertArtifactsUnchanged();
    }

    [TestMethod]
    [DataRow("song")]
    [DataRow("reference")]
    [DataRow("lyrics")]
    [DataRow("timings")]
    [DataRow("prompt")]
    [DataRow("settings")]
    [DataRow("pins")]
    [DataRow("weight-hash")]
    [DataRow("unknown-worker")]
    [DataRow("state-adapter")]
    [DataRow("memory-adapter")]
    public async Task LegacyCompatibilityCannotHideAnyOtherChangedInput(string changed)
    {
        using var fixture = new Fixture();
        var adapters = CurrentAdapters(); adapters[Worker] = LegacyWorker;
        if (changed == "unknown-worker") adapters[Worker] = new('A', 64);
        if (changed == "state-adapter") adapters["AIHub.SongClip.MotionState"] = new('B', 64);
        if (changed == "memory-adapter") adapters["AIHub.H3Memory"] = new('C', 64);
        fixture.WriteLegacyBinding(adapters);
        var before = File.ReadAllBytes(fixture.Binding);
        if (changed == "song") File.AppendAllText(fixture.Store.Resolve(fixture.Attempt.ProjectId, fixture.Attempt.Song), "changed");
        if (changed == "reference") File.AppendAllText(fixture.Store.Resolve(fixture.Attempt.ProjectId, fixture.Attempt.Reference), "changed");
        if (changed == "lyrics") fixture.Attempt = fixture.Attempt with { Lyrics = "Changed lyrics" };
        if (changed == "timings") fixture.Attempt = fixture.Attempt with { Timings = "[00:02.00]Changed" };
        if (changed == "prompt") fixture.Attempt = fixture.Attempt with { Settings = fixture.Attempt.Settings with { Prompt = "Changed prompt" } };
        if (changed == "settings") fixture.Attempt.Settings.Motion.Steps++;
        if (changed == "pins") fixture.Cards[0].Revision = new('d', 40);
        if (changed == "weight-hash") fixture.Cards[0].Files[0].Sha256 = new('e', 64);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(fixture.EnsureAsync);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding));
        Assert.IsFalse(File.Exists(fixture.Compatibility)); fixture.AssertArtifactsUnchanged();
    }

    [TestMethod]
    public async Task NewPlanningVersionNeverAdoptsTheLegacyAdapterIdentity()
    {
        using var fixture = new Fixture();
        fixture.Attempt = fixture.Attempt with { PlanningVersion = 1 };
        fixture.WriteLegacyBinding();
        var before = File.ReadAllBytes(fixture.Binding);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(fixture.EnsureAsync);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding));
        Assert.IsFalse(File.Exists(fixture.Compatibility)); fixture.AssertArtifactsUnchanged();
    }

    [TestMethod]
    public async Task AlteredCompatibilityReceiptIsRejectedAndPreserved()
    {
        using var fixture = new Fixture(); fixture.WriteLegacyBinding(); await fixture.EnsureAsync();
        File.WriteAllText(fixture.Compatibility, "{\"Schema\":1,\"LegacyInputHash\":\"unknown\"}");
        var evidenceBefore = File.ReadAllBytes(fixture.Compatibility); var before = File.ReadAllBytes(fixture.Binding);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(fixture.EnsureAsync);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Binding));
        CollectionAssert.AreEqual(evidenceBefore, File.ReadAllBytes(fixture.Compatibility)); fixture.AssertArtifactsUnchanged();
    }

    private static Dictionary<string, string> CurrentAdapters()
    {
        var result = new Dictionary<string, string>();
        foreach (var resource in new[] { Worker, "AIHub.SongClip.MotionState", "AIHub.H3Memory" })
        {
            using var input = typeof(H3MusicRunner).Assembly.GetManifestResourceStream(resource)!;
            result.Add(resource, Convert.ToHexString(SHA256.HashData(input)));
        }
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicProjectTests.Files _files = new();
        internal SongClipStore Store { get; }
        internal SongClipAttempt Attempt { get; set; }
        internal IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
        private readonly string _job;
        internal string Binding => Path.Combine(_job, "input-binding.json");
        internal string Compatibility => Path.Combine(_job, "input-binding-compatibility.json");
        internal string LegacyHash { get; private set; } = "";

        internal Fixture()
        {
            Store = new(Path.Combine(_files.Root, "projects"));
            Attempt = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "attachments/song.wav", "attachments/reference.png",
                "Legacy fixture", "A cat returns home", "[00:01.00]A cat returns home", new(), _files.Root, 8)
                { Model = H3MusicRunner.ModelId };
            Directory.CreateDirectory(Path.GetDirectoryName(Store.Resolve(Attempt.ProjectId, Attempt.Song))!);
            File.WriteAllBytes(Store.Resolve(Attempt.ProjectId, Attempt.Song), [1, 2, 3]);
            File.WriteAllBytes(Store.Resolve(Attempt.ProjectId, Attempt.Reference), [4, 5, 6]);
            _job = Store.Resolve(Attempt.ProjectId, "attempts/" + Attempt.Id); Directory.CreateDirectory(_job);
            File.WriteAllBytes(Path.Combine(_job, "clip.mp4"), [7, 8, 9]);
            File.WriteAllText(Path.Combine(_job, "worker-result.json"), "{\"finished\":true}");
            Cards = H3MusicComponents.Cards(_files.Root);
        }
        internal Task EnsureAsync() => H3MusicAttemptBinding.EnsureAsync(Attempt, Store, _job, Cards, CancellationToken.None);
        internal void WriteLegacyBinding(Dictionary<string, string>? adapters = null)
        {
            if (adapters is null) { adapters = CurrentAdapters(); adapters[Worker] = LegacyWorker; }
            string HashAttachment(string relative) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Store.Resolve(Attempt.ProjectId, relative))));
            var identity = new
            {
                attempt = Attempt,
                songSha256 = HashAttachment(Attempt.Song), referenceSha256 = HashAttachment(Attempt.Reference),
                pins = Cards.OrderBy(c => c.ModelArtifactId, StringComparer.Ordinal).Select(c => new
                {
                    c.ModelArtifactId, c.Revision,
                    files = c.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => new { f.RelativePath, f.SizeBytes, f.Sha256 })
                }),
                adapters
            };
            LegacyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));
            File.WriteAllText(Binding, JsonSerializer.Serialize(new { Schema = 1, InputHash = LegacyHash }));
        }
        internal void AssertArtifactsUnchanged()
        {
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(_job, "clip.mp4")));
            Assert.AreEqual("{\"finished\":true}", File.ReadAllText(Path.Combine(_job, "worker-result.json")));
        }
        public void Dispose() => _files.Dispose();
    }
}
