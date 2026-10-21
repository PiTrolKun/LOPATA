using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraAceCatalogNativeTests
{
    [TestMethod]
    public async Task PublicWorkerAppliesExpandedCatalogAndKeepsConversionReceipt()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_ACE_INTEGRATED");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Explicit integrated ACE stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        var definitions = MusicLoraCatalog.For(MusicAceCatalog.Variation);
        foreach (var d in definitions.Where(d => d.Format == "audio-sliders-peft-v1")) {
            var card = d.Card(root); Directory.CreateDirectory(card.InstallDirectory);
            foreach (var f in d.Files) {
                var destination = Path.Combine(card.InstallDirectory, f.RelativePath);
                if (!File.Exists(destination)) File.Copy(Path.Combine(stand, "downloads", f.RelativePath), destination);
            }
        }
        var uses = definitions.Select(d => new MusicLoraUse(d.Id, d.Revision, d.Id == "jay2" ? .4 : .2)).ToArray();
        Assert.IsTrue(await MusicLoraAssets.PrepareAsync(root, uses, false, null, default));
        var expert = MusicAceCatalog.Defaults();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) expert.Values[key] = 0;
        var request = new MusicYueRequest("jay_singing_runs, pop, piano, male vocal", "[Verse]\nWe carry sparks across the rain", 918, 514, 8) { Expert = expert, Loras = uses };
        var worker = new MusicAceWorker();
        worker.Log += line => File.AppendAllText(Path.Combine(stand, "integrated.log"), line + "\n");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var output = Path.Combine(stand, "integrated.wav");
        await worker.SynthesizeAsync(MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Variation), "",
            request, Path.Combine(stand, "integrated.json"), output, cancel.Token);
        Assert.IsTrue(MusicWaveFile.ReadDuration(output) >= TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(output + ".receipt.json", worker.LastRequestReceipt!, cancel.Token);
        using var receipt = JsonDocument.Parse(worker.LastRequestReceipt!);
        var applied = receipt.RootElement.GetProperty("loras"); Assert.AreEqual(13, applied.GetArrayLength());
        foreach (var d in definitions.Where(d => d.Format == "audio-sliders-peft-v1")) {
            var item = applied.EnumerateArray().Single(x => x.GetProperty("id").GetString() == d.Id);
            Assert.AreEqual(704, item.GetProperty("adaptation").GetProperty("verified_tensors").GetInt32());
            foreach (var f in d.Files) {
                await using var stream = File.OpenRead(Path.Combine(d.Card(root).InstallDirectory, f.RelativePath));
                Assert.AreEqual(f.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel.Token)).ToLowerInvariant());
            }
        }
        Assert.IsFalse(Directory.EnumerateDirectories(Path.GetTempPath(), "lopata-lora-format-*").Any());
        await File.WriteAllTextAsync(Path.Combine(stand, "integrated-pass.json"), JsonSerializer.Serialize(new { pass = true, adapters = 13, converted = 12, verifiedTensorsPerAdapter = 704 }));
    }

    [TestMethod]
    public async Task NewAceCatalogAdaptersReallyGenerateAudio()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_ACE_CATALOG");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Explicit native lifecycle stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        stand = Path.Combine(stand, "ace-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(stand);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var candidates = JsonSerializer.Deserialize<MusicLoraDefinition[]>(File.ReadAllText(Path.Combine(stand, "..", "candidate-catalog.json")))!;
        object Use(MusicLoraDefinition d, double strength) => new { id = d.Id, revision = d.Revision, strength,
            format = d.Format, directory = Path.Combine(stand, "..", "downloads"),
            files = d.Files.Select(f => new { name = f.RelativePath, sha256 = f.Sha256 }).ToArray() };
        var cases = candidates.Select(d => (d.Id, new[] { Use(d, .5) })).ToList();
        cases.Add(("mix", new[] { Use(candidates.Single(d => d.Id == "ace-slider-energy"), .35), Use(candidates.Single(d => d.Id == "ace-slider-vintage"), .6) }));
        cases.Add(("off", Array.Empty<object>()));
        await MusicAceSource.VerifyAsync(lifetime.Token);
        var overlay = await MusicAceRuntime.PrepareAsync(root, lifetime.Token,
            line => File.AppendAllText(Path.Combine(stand, "preparation.log"), line + "\n"));
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", lifetime.Token, new HashSet<string> {
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonXpuId });
        var expert = MusicAceCatalog.Defaults();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) expert.Values[key] = 0;
        var request = new MusicYueRequest("instrumental, mellow jazz piano trio, brushed drums",
            "[instrumental]", 918, 514, 8) { Expert = expert };
        var results = new List<object>(); var hashes = new Dictionary<string, string>();
        foreach (var (name, uses) in cases) {
            var payload = MusicAceRequest.Build(request); payload["loras"] = uses;
            var input = Path.Combine(stand, name + ".json"); var output = Path.Combine(stand, name + ".wav");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(payload), lifetime.Token);
            var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE" }) info.Environment[key] = "1";
            info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
            ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_ace_worker.py"),
                overlay.Overlay, MusicAceSource.DirectoryPath, MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Variation),
                MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Companions), input, output, runtime.Device, "generate"]);
            var lines = new List<string>();
            async Task Drain(StreamReader reader) {
                while (await reader.ReadLineAsync() is { } line) lock (lines) {
                    lines.Add(line); File.AppendAllText(Path.Combine(stand, "live.log"), name + " " + line + "\n");
                }
            }
            using var process = OwnedProcessRegistry.Shared.Start(info, "Music.ACE.LoRAStand");
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            var clock = Stopwatch.StartNew();
            try {
                await process.WaitForExitAsync(lifetime.Token); await Task.WhenAll(stdout, stderr);
                Assert.AreEqual(0, process.ExitCode, string.Join("\n", lines.TakeLast(15)));
                Assert.IsTrue(MusicWaveFile.ReadDuration(output) >= TimeSpan.FromSeconds(1));
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".receipt.json", lifetime.Token));
                var applied = receipt.RootElement.GetProperty("loras"); Assert.AreEqual(uses.Length, applied.GetArrayLength());
                foreach (var adapter in applied.EnumerateArray()) {
                    Assert.AreEqual("audio-sliders-peft-v1", adapter.GetProperty("adaptation").GetProperty("recipe").GetString());
                    Assert.AreEqual(352, adapter.GetProperty("adaptation").GetProperty("layers").GetInt32());
                    Assert.IsFalse(adapter.TryGetProperty("directory", out _));
                }
                Console.WriteLine("Native ACE passed: " + name);
                hashes[name] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output, lifetime.Token)));
                results.Add(new { name, pass = true, count = applied.GetArrayLength(), seconds = clock.Elapsed.TotalSeconds, hash = hashes[name], runtime.Device });
            } finally {
                if (!process.HasExited) await OwnedProcessRegistry.Shared.StopAndWaitAsync(process.Id);
                await Task.WhenAll(stdout, stderr); File.WriteAllLines(Path.Combine(stand, name + ".log"), lines);
                await File.WriteAllTextAsync(Path.Combine(stand, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        foreach (var hash in hashes.Where(h => h.Key != "off")) Assert.AreNotEqual(hashes["off"], hash.Value, hash.Key);
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "Music.ACE.LoRAStand"));
    }
}
