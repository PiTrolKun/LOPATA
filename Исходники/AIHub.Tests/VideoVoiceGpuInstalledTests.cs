using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceGpuInstalledTests
{
    [TestMethod]
    [DataRow("kokoro-sveta")][DataRow("kokoro-heart")][DataRow("kitten2")][DataRow("vosk010")]
    [DataRow("supertonic3")][DataRow("qwen3-17b")][DataRow("qwen3-06b")][DataRow("orpheus3b")]
    [DataRow("silero56")][DataRow("cosyvoice300")]
    public async Task ExplicitCpuGpuComparison(string id)
    {
        var selected = Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_TEST");
        var root = Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_OUTPUT");
        var modelsRoot = Environment.GetEnvironmentVariable("LOPATA_VOICE_MODELS_ROOT");
        if (selected?.Split(',').Contains(id) != true || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(modelsRoot))
        { Assert.Inconclusive("Explicit GPU benchmark invocation required."); return; }
        var work = Path.Combine(root, id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(work);
        var library = Path.Combine(work, "library"); Directory.CreateDirectory(Path.Combine(library, "Entries"));
        foreach (var file in Directory.EnumerateFiles(AppDataPaths.ManagedModelLibraryEntriesDirectory, "*.json"))
            File.Copy(file, Path.Combine(library, "Entries", Path.GetFileName(file)));
        using var audit = ComponentAudit.UseTestStore(Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_AUDIT") ?? Path.Combine(root, "audit"), false);
        using var operation = ComponentAudit.CaptureOperation();
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            var store = new ManagedModelLibraryStore(library);
            using var prepare = new VideoModelPreparation(store);
            if (!await prepare.PrepareAsync(modelsRoot, new("speech", [id]), false, null, deadline.Token))
            {
                Assert.AreEqual("1", Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_PREPARE"), "Explicit component preparation authorization required.");
                var seed = Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_SEED");
                if (!string.IsNullOrWhiteSpace(seed))
                    foreach (var card in VideoModelPreparation.Cards(modelsRoot, new("speech", [id])))
                        foreach (var file in card.Files)
                        {
                            var source = RuntimeFileAudit.Contained(seed, file.RelativePath);
                            var target = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
                            if (!File.Exists(source) || File.Exists(target)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
                        }
                Assert.IsTrue(await prepare.PrepareAsync(modelsRoot, new("speech", [id]), true, null, deadline.Token));
            }
            // Preflight the common hardware bundles outside the measured generation.
            await ManagedPythonRuntime.ResolveAsync("cpu", deadline.Token);
            await ManagedPythonRuntime.ResolveAsync("auto", deadline.Token);
            var model = VideoNarratorCatalog.Get(id); var language = model.Languages.Contains("ru") ? "ru" : "en";
            var text = language == "ru" ? "Привет, мир. Это проверка скорости диктора." : "Hello world. This is a narrator speed test.";
            var rows = new List<object>();
            using var benchmark = VideoNarratorBenchmark.Capture(2);
            foreach (var device in new[] { "cpu", "gpu" })
            {
                using var narrator = new VideoNarratorRegistry(store).Create(id);
                var clock = Stopwatch.StartNew();
                var audio = narrator is IConfiguredVideoNarrator configured
                    ? await configured.GenerateAsync(id, language, text, 100, new(model.DefaultSpeaker, Device: device), deadline.Token)
                    : await narrator.GenerateAsync(id, language, text, 100, deadline.Token);
                clock.Stop();
                using var wave = new NAudio.Wave.WaveFileReader(audio.Path);
                Assert.IsTrue(wave.Length > 0 && wave.TotalTime.TotalSeconds > 0.1);
                var execution = Path.Combine(Path.GetDirectoryName(audio.Path)!, "execution.json");
                using var facts = JsonDocument.Parse(await File.ReadAllTextAsync(execution));
                var isGpu = facts.RootElement.GetProperty("isGpu").GetBoolean();
                if (device == "gpu") Assert.IsTrue(isGpu, "The GPU trial must not silently fall back to CPU.");
                else Assert.IsFalse(isGpu, "The CPU control must not use GPU.");
                narrator.Dispose(); // Finish and close the owned diagnostic log before reading it.
                var log = Path.Combine(Path.GetDirectoryName(audio.Path)!, "worker.log");
                if (id == "orpheus3b" && device == "gpu")
                    StringAssert.Matches(await File.ReadAllTextAsync(log), new System.Text.RegularExpressions.Regex(@"offloaded [1-9][0-9]*/[0-9]+ layers to GPU"));
                File.Copy(log, Path.Combine(work, device + "-worker.log"));
                File.Copy(audio.Path, Path.Combine(work, device + ".wav"));
                rows.Add(new { device, isGpu, elapsedSeconds = clock.Elapsed.TotalSeconds, audioSeconds = wave.TotalTime.TotalSeconds,
                    rtf = clock.Elapsed.TotalSeconds / wave.TotalTime.TotalSeconds, audio.Models,
                    execution = File.Exists(execution) ? JsonDocument.Parse(await File.ReadAllTextAsync(execution)).RootElement.Clone() : (JsonElement?)null });
                await File.WriteAllTextAsync(Path.Combine(work, "result.json"), JsonSerializer.Serialize(new { id, language, text, rows }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(work, "failure.txt"), error.ToString()); throw; }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
}
