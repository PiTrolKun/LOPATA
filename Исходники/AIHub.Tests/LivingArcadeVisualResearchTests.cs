using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

/// <summary>Opt-in, isolated, stateless research. Never writes a user's run or invents consent.</summary>
[TestClass, DoNotParallelize]
public sealed class LivingArcadeVisualResearchTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task ProcessExplicitVisualResearchQueue()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_ARCADE_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(root)) { Assert.Inconclusive("Explicit visual research only."); return; }
        root = Path.GetFullPath(root);
        foreach (var dir in new[] { "jobs", "cases", "raw" }) Directory.CreateDirectory(Path.Combine(root, dir));
        var license = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var previousBackground = ApplicationBackgroundOperations.Current;
        Assert.IsNull(previousBackground);
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(root, "background", "state.json")));
        ComponentLicenseGate.ConfirmAsync = (ids, ct) => license.EnsureAsync(
            ComponentLicenseService.ExpandSelection(ids, license.Entries), _ => Task.FromResult(false), ct);
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()));
        runtime.ConfigurePrivateNoRetry();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(50));
        string? loaded = null;
        try
        {
            await EngineSetService.Shared.InitializeAsync(deadline.Token);
            var ready = await LivingArcadeModels.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 4, false,
                new Inline<LivingArcadePreparationProgress>(p => TestContext.WriteLine(p.StageKey)), deadline.Token);
            Assert.IsTrue(ready.Ready, ready.StatusKey);
            await LivingArcadeModels.VerifyForUseAsync(ready.Paths!, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), "{\"ready\":true}", deadline.Token);
            while (!deadline.IsCancellationRequested && !File.Exists(Path.Combine(root, "STOP")))
            {
                var file = Directory.GetFiles(Path.Combine(root, "jobs"), "*.json").OrderBy(x => x, StringComparer.Ordinal)
                    .FirstOrDefault(p => !File.Exists(Path.Combine(root, "cases", Path.GetFileName(p))));
                if (file is null) { await Task.Delay(500, deadline.Token); continue; }
                var job = JsonNode.Parse(await File.ReadAllTextAsync(file, deadline.Token))!.AsObject();
                var id = job["id"]!.GetValue<string>();
                var modelKey = job["model"]!.GetValue<string>();
                var artist = modelKey == "3B";
                var gemma = modelKey == "Gemma";
                string? gemmaPath = null;
                if (gemma)
                {
                    var check = await LetItOutModel.CheckAsync(new StorageSettingsStore().LoadOrCreate(), deadline.Token);
                    Assert.AreEqual(CoreModelAvailability.Installed, check.Availability);
                    gemmaPath = check.ModelPath!;
                    await LetItOutModel.VerifyForUseAsync(gemmaPath, deadline.Token);
                }
                var model = new DebugModelInfo {
                    Name = gemma ? LetItOutModel.DisplayName : $"Qwen2.5-Coder {modelKey} Instruct", Format = "gguf",
                    Path = gemma ? gemmaPath! : artist ? ready.Paths!.Artist : ready.Paths!.Main,
                    SizeBytes = gemma ? LetItOutModel.TotalBytes : artist ? LivingArcadeModels.ArtistBytes : LivingArcadeModels.MainBytes,
                    Role = artist ? "living_arcade_artist" : "living_arcade_designer"
                };
                var result = new JsonObject { ["id"] = id, ["model"] = model.Name,
                    ["title"] = job["title"]!.GetValue<string>(), ["producer"] = "LOPATA",
                    ["scenario"] = "LivingArcade visual research", ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["modelRevision"] = gemma ? ManagedModelCatalog.LetItOutRevision : artist ? LivingArcadeModels.ArtistRevision : LivingArcadeModels.MainRevision };
                var watch = Stopwatch.StartNew();
                try
                {
                    using var callDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    callDeadline.CancelAfter(TimeSpan.FromMinutes(3));
                    var requestedContext = job["context"]?.GetValue<int>() ?? 16384;
                    var placementKey = modelKey + ":" + requestedContext;
                    if (loaded != placementKey)
                    {
                        await runtime.PreparePoetryAsync(model, callDeadline.Token, requestedContextTokens: requestedContext,
                            reasoningOptions: MusicPoetryReasoningOptions.Disabled,
                            gpuAdmission: (a, ct) => Task.FromResult(a.Reason == "cpu_fallback" ? LlamaGpuDecision.Cpu : LlamaGpuDecision.Hybrid));
                        loaded = placementKey;
                    }
                    var output = job["maxTokens"]?.GetValue<int>() ?? 4000;
                    var request = JsonSerializer.Serialize(new {
                        messages = new[] { new { role = "system", content = job["system"]!.GetValue<string>() },
                            new { role = "user", content = job["prompt"]!.GetValue<string>() } },
                        temperature = job["temperature"]?.GetValue<double>() ?? .8,
                        seed = job["seed"]?.GetValue<int>() ?? 46, top_p = .9, top_k = 40, repeat_penalty = 1.05,
                        max_tokens = output, stream = false, response_format = job["free"]?.GetValue<bool>() == true ? null : new { type = "json_object" },
                        chat_template_kwargs = new { enable_thinking = false }
                    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                    await File.WriteAllTextAsync(Path.Combine(root, "raw", id + ".request.json"), request, callDeadline.Token);
                    var raw = await runtime.GenerateBoundedPrivateJsonAsync(model, request, output, TestContext.WriteLine, callDeadline.Token)
                        ?? throw new InvalidDataException("Context budget rejected before inference.");
                    await File.WriteAllTextAsync(Path.Combine(root, "raw", id + ".response.json"), raw, deadline.Token);
                    var response = JsonNode.Parse(raw)!;
                    var choice = response["choices"]![0]!;
                    result["finishReason"] = choice["finish_reason"]!.GetValue<string>();
                    result["usage"] = response["usage"]?.DeepClone();
                    result["runtime"] = JsonSerializer.SerializeToNode(runtime.PoetrySession);
                    var content = choice["message"]!["content"]!.GetValue<string>();
                    await File.WriteAllTextAsync(Path.Combine(root, "raw", id + ".content.txt"), content, deadline.Token);
                    if (result["finishReason"]!.GetValue<string>() != "stop") throw new InvalidDataException("Incomplete model output: " + result["finishReason"]);
                    if (content.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        content = content.Trim();
                        content = content[(content.IndexOf('\n') + 1)..];
                        var end = content.LastIndexOf("```", StringComparison.Ordinal);
                        if (end >= 0) content = content[..end];
                        result["transportFenceRemoved"] = true;
                    }
                    var answer = JsonNode.Parse(content)!.AsObject();
                    foreach (var item in answer) if (!result.ContainsKey(item.Key)) result[item.Key] = item.Value?.DeepClone();
                    result["status"] = "generated";
                }
                catch (Exception error) when (error is not OperationCanceledException || !deadline.IsCancellationRequested)
                { result["status"] = "failed"; result["error"] = error.GetType().Name + ": " + error.Message; }
                result["elapsedSeconds"] = watch.Elapsed.TotalSeconds;
                await File.WriteAllTextAsync(Path.Combine(root, "cases", Path.GetFileName(file)), result.ToJsonString(new() { WriteIndented = true }));
                TestContext.WriteLine($"CASE {id}: {result["status"]} {watch.Elapsed.TotalSeconds:F2}s");
            }
        }
        finally
        {
            await runtime.RetirePoetryAsync();
            ApplicationBackgroundOperations.Current = previousBackground;
            ComponentLicenseGate.ConfirmAsync = previousGate;
            await File.WriteAllTextAsync(Path.Combine(root, "retired.json"), JsonSerializer.Serialize(new { retired = runtime.PoetrySession is null, utc = DateTimeOffset.UtcNow }));
        }
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
