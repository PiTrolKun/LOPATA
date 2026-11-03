using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

/// <summary>Opt-in use of the installed model. The projection limits memory, not GPU compute speed.</summary>
[TestClass, DoNotParallelize]
public sealed class LetItOutGemmaNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task InstalledGemmaPreparesAndStreamsInAllAutomaticModes()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_LET_IT_OUT_NATIVE_REPORT");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt-in installed Gemma integration test."); return; }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(output, "audit"), false);
        var log = new List<string>();
        var readiness = await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 2,
            false, new Inline<LetItOutChatPreparationProgress>(p => log.Add(p.StageKey)), timeout.Token);
        Assert.IsTrue(readiness.Ready, readiness.StatusKey);
        var path = readiness.CoreModelPath!;
        var metadata = LiteraryModelMemoryMetadata.Read(path);
        var policy = LetItOutMemoryPolicy.Read(path, metadata);
        await File.WriteAllTextAsync(Path.Combine(output, "memory-policy.json"), JsonSerializer.Serialize(new {
            policy.FileBytes, policy.KvBytes, policy.ExpertLayers, policy.FullGpuBytes, policy.FullGpuRequired,
            reserve = LetItOutMemoryPolicy.VramReserve }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (expected, capacity) in new[] { ("Gpu", 0L), ("Hybrid", 12L * 1024 * 1024 * 1024), ("Cpu", 2L * 1024 * 1024 * 1024) })
        {
            var modes = new List<string>(); var turns = new List<object>();
            using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()));
            if (capacity > 0) runtime.LetItOutMemoryProjection = actual => actual with {
                TotalBytes = capacity, FreeBytes = Math.Max(0, capacity - (actual.TotalBytes - actual.FreeBytes)) };
            runtime.ConfigureLetItOut((_, _) => Task.FromResult(LlamaGpuDecision.Cancel), modes.Add);
            var model = new DebugModelInfo { Path = path, Name = LetItOutModel.DisplayName, Format = "gguf", Role = "chat" };
            var messages = new List<LetItOutChatMessage>();
            double warmSeconds = 0; int? warmPid = null;
            try
            {
                await LetItOutModel.VerifyForUseAsync(path, timeout.Token);
                var loading = Stopwatch.StartNew();
                await runtime.WarmLetItOutAsync(model, timeout.Token);
                warmSeconds = loading.Elapsed.TotalSeconds;
                warmPid = OwnedProcessRegistry.Shared.GetSnapshot().Single(p => p.Component == "LlamaServerRuntimeService").Pid;
                await runtime.WarmLetItOutAsync(model, timeout.Token);
                Assert.AreEqual(warmPid, OwnedProcessRegistry.Shared.GetSnapshot().Single(p => p.Component == "LlamaServerRuntimeService").Pid,
                    "Repeated entry must reuse an already loaded model and recheck its reserve.");
                Assert.IsTrue(modes.Contains(expected), string.Join(",", modes));
                foreach (var question in new[] { "Привет", "Я уже третий выходной подряд помогаю другу с ремонтом. Он даже не спрашивает, удобно ли мне. Я начинаю злиться, хотя сам обещал ему помочь. Как ты на это смотришь?" })
                {
                    messages.Add(new("user", question)); var chunks = new StringBuilder();
                    var watch = Stopwatch.StartNew(); double? first = null;
                    var request = new LetItOutChatRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), path, messages.ToArray(), "ru");
                    var response = await runtime.GenerateLetItOutAsync(model, LetItOutChatRuntime.BuildPayload(request),
                        LetItOutChatRuntime.AnswerTokens, LetItOutChatRuntime.CreateAnswerProgress(new Inline<ModelStreamChunk>(chunk => {
                            if (chunk.Text.Length > 0) { first ??= watch.Elapsed.TotalSeconds; chunks.Append(chunk.Text); }
                        })), timeout.Token);
                    var answer = LetItOutChatRuntime.ValidateResult(response);
                    Assert.IsFalse(answer.Contains("<|channel>", StringComparison.Ordinal) || answer.Contains("<channel|>", StringComparison.Ordinal), answer);
                    Assert.AreEqual(answer, chunks.ToString().Trim());
                    Assert.IsTrue(modes.Contains(expected), string.Join(",", modes));
                    Assert.AreEqual(warmPid, OwnedProcessRegistry.Shared.GetSnapshot().Single(p => p.Component == "LlamaServerRuntimeService").Pid,
                        "The first and following answers must reuse the warmed model process.");
                    messages.Add(new("assistant", answer));
                    turns.Add(new { question, answer, firstSeconds = first, elapsedSeconds = watch.Elapsed.TotalSeconds });
                }
            }
            finally
            {
                await runtime.RetireLetItOutAsync();
                await File.WriteAllTextAsync(Path.Combine(output, expected + ".json"), JsonSerializer.Serialize(new {
                    expected, capacity, modes, warmSeconds, warmPid, turns }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "LlamaServerRuntimeService"));
        TestContext.WriteLine("Preparation, exact model/prompt, streamed two-turn conversation, automatic GPU/hybrid/CPU and retirement verified.");
    }

    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
