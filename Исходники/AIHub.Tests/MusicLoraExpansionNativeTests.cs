using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

/// <summary>Explicit local stand, never downloads or starts a model in the ordinary test suite.</summary>
[TestClass, DoNotParallelize]
public sealed class MusicLoraExpansionNativeTests
{
    [TestMethod]
    public async Task ExpandedStudioCatalogActuallyPatchesEveryDeclaredStage()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_EXPANSION");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Opt-in expansion stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        var candidates = JsonSerializer.Deserialize<MusicLoraDefinition[]>(await File.ReadAllTextAsync(Path.Combine(stand, "candidate-catalog.json")))!;
        using var downloads = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stand, "downloads.json")));
        foreach (var d in candidates) {
            Assert.AreEqual(d.Revision, MusicLoraCatalog.Find(d.Id).Revision);
            var card = d.Card(root); Directory.CreateDirectory(card.InstallDirectory);
            foreach (var file in card.Files) {
                var source = downloads.RootElement.EnumerateArray().Single(x => x.GetProperty("sha256").GetString() == file.Sha256).GetProperty("local").GetString()!;
                var destination = Path.Combine(card.InstallDirectory, file.RelativePath);
                if (!File.Exists(destination)) File.Copy(source, destination);
            }
        }
        Assert.IsTrue(await MusicLoraAssets.PrepareAsync(root, candidates.Select(d => new MusicLoraUse(d.Id, d.Revision, d.Default)).ToArray(), false, null, default));
        var output = Path.Combine(stand, "native-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(output);
        var results = new List<object>(); var errors = new List<string>();
        var settings = MusicModelVariants.Defaults(MusicStudioRuntime.Variation); settings.Values["cot"] = 2;
        var request = new MusicYueRequest("pop rock, warm piano, steady drums, female vocal", "[Verse]\nWe follow sparks across the rain\nAnd find our way back home again", 918, 514, 8) {
            Expert = settings, StudioRuntimeRevision = MusicStudioRuntime.Current.Revision, StudioRuntimePack = MusicStudioRuntime.Current.Pack };
        var model = MusicModelVariants.Artifact(root, MusicStudioRuntime.Variation, MusicComponentCatalog.ModelId);
        var decoder = MusicModelVariants.Artifact(root, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId);
        async Task<string?> Run(string name, MusicYueRequest input, bool plan = false)
        {
            var worker = new MusicStudioWorker(); var lines = new List<string>();
            worker.Log += line => { lock (lines) { lines.Add(line); File.AppendAllText(Path.Combine(output, "live.log"), name + " " + line + "\n"); } };
            var path = Path.Combine(output, name + (plan ? ".abc" : ".wav")); var started = DateTimeOffset.Now;
            try {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                if (plan) await worker.PlanAsync(model, input, Path.Combine(output, name + ".json"), path, cancel.Token);
                else await worker.SynthesizeAsync(model, decoder, input, Path.Combine(output, name + ".json"), path, cancel.Token);
                foreach (var use in input.Loras) foreach (var slot in MusicLoraCatalog.Find(use.Id).Slots.Keys.Where(s => !plan || s == "ar")) {
                    Assert.IsTrue(lines.Any(line => line.Contains("[Adapter] " + slot.ToUpperInvariant() + " ") && line.Contains("/" + use.Id)
                        && Regex.IsMatch(line, @": [1-9][0-9]* tensors at scale ")), name + " did not patch " + slot);
                }
                if (!plan) Assert.IsTrue(MusicWaveFile.ReadDuration(path) >= TimeSpan.FromSeconds(1));
                if (worker.LastRequestReceipt is not null) await File.WriteAllTextAsync(path + ".receipt.json", worker.LastRequestReceipt);
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                results.Add(new { name, pass = true, hash, seconds = (DateTimeOffset.Now - started).TotalSeconds });
                return hash;
            } catch (Exception error) {
                errors.Add(name + ": " + error.Message); results.Add(new { name, pass = false, error = error.ToString() }); return null;
            } finally {
                lock (lines) File.WriteAllLines(Path.Combine(output, name + ".log"), lines);
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        var baseline = await Run("off", request);
        var recheck = Environment.GetEnvironmentVariable("LOPATA_LORA_EXPANSION_RECHECK") == "1";
        var representativeIds = new[] { "industrial", "grvl-thunder", "mltnt-chanter", "ntc-metal", "instrumental-v3" };
        foreach (var d in candidates.Where(d => !recheck || representativeIds.Contains(d.Id))) {
            var hash = await Run(d.Id, request with { Loras = [new(d.Id, d.Revision, d.Default)] });
            if (hash is not null && hash == baseline) errors.Add(d.Id + ": identical to bypass");
        }
        var mix = new[] { "ntc-house", "industrial", "grvl-ember" }.Select(id => { var d = MusicLoraCatalog.Find(id); return new MusicLoraUse(id, d.Revision, .35); }).ToArray();
        await Run("mixed", request with { Loras = mix });
        var offAgain = await Run("off-again", request);
        if (offAgain != baseline || baseline is null) errors.Add("Bypass did not restore the base output.");
        foreach (var id in new[] { "ntc-metal", "grvl-thunder", "mltnt-chanter", "instrumental-v3" }) {
            var full = settings.Snapshot(); full.Values["cot"] = 0; full.Values["abc_sampling.max_tokens"] = 256;
            var d = MusicLoraCatalog.Find(id);
            await Run("plan-" + id, request with { Expert = full, Lyrics = id == "instrumental-v3" ? "[instrumental]" : request.Lyrics,
                Loras = [new(d.Id, d.Revision, d.Default)] }, true);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "errors.json"), JsonSerializer.Serialize(errors));
        Assert.IsEmpty(errors, string.Join("\n", errors));
    }
}
