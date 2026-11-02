using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipBatchReplayTests
{
    [TestMethod]
    public async Task SavedSecondBatchPassesValidationAndRetainsTheAcceptedFirstBatch()
    {
        var source = Source(); if (source is null) return;
        using var files = new MusicProjectTests.Files();
        Copy(source, files.Root);
        var attempt = Read<SongClipAttempt>(Path.Combine(source, "snapshot.json"));
        var checkpoint = Read<JsonElement>(Path.Combine(source, "visual-plan-progress.json"));
        var first = checkpoint.GetProperty("Batches")[0];
        var expected = SongClipVisualProtocol.Slots(attempt, 305).Skip(6).Take(6).ToArray();
        foreach (var name in new[] { "planner-002-1.json", "planner-002-2.json" }) {
            var reply = Read<VideoStructuredAnswer>(Path.Combine(source, name));
            var parsed = SongClipVisualProtocol.ParseAndValidate(reply.Text, attempt, 305, [reply.Model], expected);
            Assert.AreNotEqual(first.GetProperty("globalStyle").GetString(), parsed.GlobalStyle);
            Assert.AreEqual(first.GetProperty("globalStyle").GetString(), parsed.GlobalStyle.Replace('\u2019', '\''));
        }
        var preserved = File.ReadAllBytes(Path.Combine(files.Root, "planner-001-1.json")); var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => SongClipPlanning.EnsureCoreAsync(attempt, files.Root, 305,
            checkpoint.GetProperty("InputHash").GetString()!, (_, _, _, _) => {
                if (++calls > 1) throw new OperationCanceledException();
                return Task.FromResult(Read<VideoStructuredAnswer>(Path.Combine(source, "planner-002-1.json")));
            }, (_, _) => { }, CancellationToken.None, prepareStory: true));
        var resumed = Read<JsonElement>(Path.Combine(files.Root, "visual-plan-progress.json"));
        Assert.AreEqual(2, resumed.GetProperty("Batches").GetArrayLength());
        Assert.IsTrue(JsonElement.DeepEquals(first, resumed.GetProperty("Batches")[0]));
        CollectionAssert.AreEqual(preserved, File.ReadAllBytes(Path.Combine(files.Root, "planner-001-1.json")));
    }

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task InstalledPlannerCompletesAllRemainingBatchesOnAnIsolatedCopy()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SONG_BATCH_INSTALLED") != "1") {
            Assert.Inconclusive("Explicit LOPATA_SONG_BATCH_INSTALLED=1 opt-in is required."); return;
        }
        var source = Source(); if (source is null) return;
        var evidence = Environment.GetEnvironmentVariable("LOPATA_SONG_BATCH_EVIDENCE");
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence));
        evidence = Path.GetFullPath(evidence!);
        Assert.IsFalse(evidence.Equals(source, StringComparison.OrdinalIgnoreCase)
            || evidence.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(evidence), "Use a fresh isolated evidence directory.");
        Directory.CreateDirectory(evidence); Copy(source, evidence);
        var attempt = Read<SongClipAttempt>(Path.Combine(source, "snapshot.json"));
        var checkpoint = Read<JsonElement>(Path.Combine(source, "visual-plan-progress.json"));
        var preserved = File.ReadAllBytes(Path.Combine(evidence, "planner-001-1.json"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        SongClipPlannerSession? session = null;
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        if (previousGate is null) ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;
        try {
            using var audit = ComponentAudit.CaptureOperation();
            var plan = await SongClipPlanning.EnsureCoreAsync(attempt, evidence, 305,
                checkpoint.GetProperty("InputHash").GetString()!, async (system, body, schema, token) => {
                    session ??= await SongClipPlannerModel.OpenAsync(attempt, token);
                    return await session.GenerateAsync(system, body, schema, SongClipPlanning.ResponseTokens, token);
                }, (_, stage) => Console.WriteLine(stage), deadline.Token, prepareStory: true);
            Assert.AreEqual(36, plan.Scenes.Length); Assert.AreEqual(305, plan.Scenes[^1].EndSeconds);
            var completed = Read<JsonElement>(Path.Combine(evidence, "visual-plan-progress.json"));
            Assert.AreEqual(6, completed.GetProperty("Batches").GetArrayLength());
            Assert.IsTrue(JsonElement.DeepEquals(checkpoint.GetProperty("Batches")[0], completed.GetProperty("Batches")[0]));
            CollectionAssert.AreEqual(preserved, File.ReadAllBytes(Path.Combine(evidence, "planner-001-1.json")));
        }
        finally {
            try { if (session is not null) await session.DisposeAsync(); }
            finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
        }
    }

    private static string? Source()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_SONG_BATCH_REPLAY");
        if (string.IsNullOrWhiteSpace(source)) { Assert.Inconclusive("Opt-in saved batch fixture is required."); return null; }
        return Path.GetFullPath(source);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))!;
    private static Task ExistingConsentOnly(IReadOnlyList<string> ids, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var receipts = service.ReadReceipts(); var catalog = service.Entries;
        foreach (var id in ComponentLicenseService.ExpandSelection(ids, catalog)) {
            var entry = catalog.Single(value => value.Id == id);
            if (!receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms))
                throw new InvalidOperationException("Existing component consent required: " + id);
        }
        return Task.CompletedTask;
    }
    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var name in new[] { "snapshot.json", "visual-story.json", "visual-plan-progress.json", "planner-001-1.json" })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name));
    }
}
