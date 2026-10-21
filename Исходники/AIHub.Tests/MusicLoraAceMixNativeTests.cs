using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraAceMixNativeTests
{
    [TestMethod]
    public async Task PinnedAceApiAppliesTwoAdaptersWithIndependentScales()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_LIFECYCLE");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Explicit native lifecycle stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        stand = Path.Combine(stand, "ace-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(stand);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var definition = MusicLoraCatalog.Find("jay2"); var card = definition.Card(root);
        Assert.IsTrue(MusicLoraAssets.Ready(definition, root), "Prepare the existing JAY_2 first; this test never downloads.");
        foreach (var file in card.Files) {
            await using var stream = File.OpenRead(Path.Combine(card.InstallDirectory, file.RelativePath));
            Assert.AreEqual(file.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream, lifetime.Token)).ToLowerInvariant());
        }
        // Private aliases exercise the actual multi-adapter API, not two public cards for one voice.
        object Use(string id, double strength) => new { id, revision = definition.Revision, strength, directory = card.InstallDirectory,
            files = definition.Files.Select(f => new { name = f.RelativePath, sha256 = f.Sha256 }).ToArray() };
        await MusicAceSource.VerifyAsync(lifetime.Token);
        var overlay = await MusicAceRuntime.PrepareAsync(root, lifetime.Token,
            line => File.AppendAllText(Path.Combine(stand, "preparation.log"), line + "\n"));
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", lifetime.Token, new HashSet<string> {
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonXpuId });
        var expert = MusicAceCatalog.Defaults();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) expert.Values[key] = 0;
        var request = new MusicYueRequest("jay_singing_runs, pop, warm piano, male vocal",
            "[Verse]\nWe follow sparks across the rain\nAnd find our way back home again", 918, 514, 8) { Expert = expert };
        var results = new List<object>(); var hashes = new Dictionary<string, string>();
        foreach (var (name, uses) in new[] {
            ("one", new[] { Use("stand-jay-single", .8) }),
            ("two", new[] { Use("stand-jay-a", .35), Use("stand-jay-b", .45) }),
            ("off", Array.Empty<object>()) }) {
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
                if (name == "two") {
                    Assert.IsTrue(lines.Any(l => l.Contains("Verified active adapters") && l.Contains("stand-jay-a") && l.Contains("stand-jay-b")));
                    var actual = applied.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("strength").GetDouble());
                    Assert.AreEqual(.35, actual["stand-jay-a"]); Assert.AreEqual(.45, actual["stand-jay-b"]);
                }
                hashes[name] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output, lifetime.Token)));
                results.Add(new { name, pass = true, count = applied.GetArrayLength(), seconds = clock.Elapsed.TotalSeconds, hash = hashes[name], runtime.Device });
            } finally {
                if (!process.HasExited) await OwnedProcessRegistry.Shared.StopAndWaitAsync(process.Id);
                await Task.WhenAll(stdout, stderr); File.WriteAllLines(Path.Combine(stand, name + ".log"), lines);
                await File.WriteAllTextAsync(Path.Combine(stand, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Assert.AreNotEqual(hashes["off"], hashes["one"]); Assert.AreNotEqual(hashes["off"], hashes["two"]);
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "Music.ACE.LoRAStand"));
    }
}
