using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

/// <summary>Opt-in replay of a saved context rejection; never writes to the source project or renders video.</summary>
[TestClass, DoNotParallelize]
public sealed class SongClipMontageContextReplayTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task SavedSongCompletesAllMontageBatchesWithTheInstalledPlanner()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_CONTEXT_SOURCE");
        if (string.IsNullOrWhiteSpace(source)) { Assert.Inconclusive("Explicit saved-source opt-in required."); return; }
        var evidence = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_CONTEXT_EVIDENCE");
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidence));
        source = Path.GetFullPath(source); evidence = Path.GetFullPath(evidence!);
        Assert.IsFalse(evidence.StartsWith(source, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(evidence), "Use a fresh isolated output directory.");
        Directory.CreateDirectory(evidence);
        foreach (var name in new[] { "snapshot.json", "visual-references.json", "visual-story.json" })
            File.Copy(Path.Combine(source, name), Path.Combine(evidence, name));
        var attempt = Read<SongClipAttempt>(source, "snapshot.json");
        var analysis = Read<SongClipMontageAnalysis>(source, "montage-analysis.json");
        var references = Read<JsonElement>(source, "visual-references.json").GetProperty("Roles")
            .Deserialize<SongClipReferenceRole[]>()!;
        var story = Read<JsonElement>(source, "visual-story.json");
        var binding = story.GetProperty("InputHash").GetString()!;
        var anchor = story.GetProperty("Anchor").GetString()!;
        var request = await SongClipPlannerModel.ResolveAsync(attempt, CancellationToken.None);
        var assistant = new VideoConceptAssistant(new UserContextService(new UserProfileStore(), new IpLocationService()));
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try {
            using var audit = ComponentAudit.CaptureOperation();
            await assistant.WarmAsync(request, deadline.Token);
            var runtime = (LlamaServerRuntimeService)typeof(VideoConceptAssistant)
                .GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(assistant)!;
            var counts = new List<object>();
            foreach (var count in new[] { 6, 3, 1 }) {
                var body = SongClipMontageProtocol.Input(attempt, analysis, analysis.Slots.Take(count).ToArray(), null, [], anchor, references);
                var used = await runtime.CountPoetryAsync(SongClipMontageProtocol.System, [new("user", body, "probe")], deadline.Token);
                counts.Add(new { scenes = count, input = used, output = 4096, headroom = 256,
                    capacity = assistant.RuntimeInfo!.Context, fits = used + 4096 + 256 <= assistant.RuntimeInfo.Context });
                File.WriteAllText(Path.Combine(evidence, $"input-{count}.json"), body);
                var original = JsonNode.Parse(body)!;
                var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
                original["knownTimingCues"] = JsonSerializer.SerializeToNode(SongClipVisualProtocol.Source(attempt).Timings, json);
                original["timingCoverage"] = "known markers only; coverage may be partial";
                var originalBody = original.ToJsonString(json);
                var originalTokens = await runtime.CountPoetryAsync(SongClipMontageProtocol.System,
                    [new("user", originalBody, "original")], deadline.Token);
                counts.Add(new { scenes = count, legacy = true, input = originalTokens, output = 4096, headroom = 256,
                    capacity = assistant.RuntimeInfo!.Context, fits = originalTokens + 4096 + 256 <= assistant.RuntimeInfo.Context });
                File.WriteAllText(Path.Combine(evidence, $"original-input-{count}.json"), originalBody);
            }
            SongClipStore.Atomic(Path.Combine(evidence, "budgets.json"), counts);
            if (Environment.GetEnvironmentVariable("LOPATA_MONTAGE_CONTEXT_PROBE_ONLY") == "1") return;
            var plan = await SongClipMontagePlanning.EnsureCoreAsync(attempt, evidence, analysis, binding,
                async (system, body, schema, token) => {
                    var used = await runtime.CountPoetryAsync(system, [new("user", body, "replay")], token);
                    using var input = JsonDocument.Parse(body);
                    File.AppendAllText(Path.Combine(evidence, "requests.jsonl"), JsonSerializer.Serialize(new {
                        used, capacity = assistant.RuntimeInfo!.Context, output = 4096,
                        slots = input.RootElement.GetProperty("slots").GetArrayLength()
                    }) + Environment.NewLine);
                    var answer = await assistant.GenerateStructuredAsync(request, system, body, schema, 4096, token);
                    return answer with { Model = answer.Model with { Role = "song-to-clip semantic planning" } };
                }, (_, stage) => Console.WriteLine(stage), deadline.Token, prepareStory: true);
            Assert.HasCount(analysis.Slots.Length, plan.Shots);
            Assert.AreEqual(analysis.DurationSeconds, plan.Shots[^1].StartSeconds + plan.Shots[^1].DurationSeconds, .000001);
            _ = SongClipMontageProtocol.Validate(plan, attempt, analysis);
        }
        finally {
            try { await assistant.RetireAsync(); }
            finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
        }
    }

    private static T Read<T>(string directory, string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory, name)))!;
    private static Task ExistingConsentOnly(IReadOnlyList<string> ids, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var receipts = service.ReadReceipts();
        foreach (var id in ComponentLicenseService.ExpandSelection(ids, service.Entries)) {
            var entry = service.Entries.Single(row => row.Id == id);
            Assert.IsTrue(receipts.Any(row => row.Id == entry.Id && row.Terms == entry.Terms), "Existing consent required: " + id);
        }
        return Task.CompletedTask;
    }
}
