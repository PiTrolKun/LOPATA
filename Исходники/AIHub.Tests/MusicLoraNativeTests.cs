using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraNativeTests
{
    [TestMethod]
    public async Task StudioNativeAdaptersProduceAudioAndCanBeBypassed()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_STAND");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Opt-in native stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        stand = Path.Combine(stand, "native-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(stand);
        var catalog = new[] { MusicLoraCatalog.Find("dreampop"), MusicLoraCatalog.Find("oldschool-hiphop") };
        foreach (var d in catalog) {
            var card = d.Card(root); Directory.CreateDirectory(card.InstallDirectory);
            var name = d.Id == "dreampop" ? "dreampop_sv_dreampop.safetensors" : "sv_oldschoolhiphop.safetensors";
            var dest = Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath);
            if (!File.Exists(dest)) File.Copy(Path.Combine(Environment.GetEnvironmentVariable("LOPATA_LORA_STAND")!, "downloads", "atomtanstudio--lora-library", name), dest);
        }
        var both = catalog.Select(d => new MusicLoraUse(d.Id, d.Revision, .7)).ToArray();
        Assert.IsTrue(await MusicLoraAssets.PrepareAsync(root, both, false, null, default));
        var settings = MusicModelVariants.Defaults(MusicStudioRuntime.Variation); settings.Values["cot"] = 2;
        var request = new MusicYueRequest("dream pop, soft drums, female vocal", "[Verse]\nWe follow sparks across the rain\nAnd find our way back home again", 918, 514, 8) {
            Expert = settings, StudioRuntimeRevision = MusicStudioRuntime.Current.Revision, StudioRuntimePack = MusicStudioRuntime.Current.Pack };
        var worker = new MusicStudioWorker(); var log = new List<string>();
        worker.Log += line => { lock (log) log.Add(DateTimeOffset.Now.ToString("o") + " " + line); };
        var cases = new[] { ("off", Array.Empty<MusicLoraUse>()), ("one", both.Take(1).ToArray()), ("two", both), ("off-again", Array.Empty<MusicLoraUse>()) };
        foreach (var (name, uses) in cases) {
            var output = Path.Combine(stand, "native-" + name + ".wav");
            try {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                await worker.SynthesizeAsync(MusicModelVariants.Artifact(root, MusicStudioRuntime.Variation, MusicComponentCatalog.ModelId),
                    MusicModelVariants.Artifact(root, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId), request with { Loras = uses },
                    Path.Combine(stand, "native-" + name + ".json"), output, cancel.Token);
                Assert.IsTrue(MusicWaveFile.ReadDuration(output) > TimeSpan.Zero);
                await File.WriteAllTextAsync(output + ".receipt.json", worker.LastRequestReceipt);
            } finally { lock (log) File.WriteAllLines(Path.Combine(stand, "native-" + name + ".log"), log); log.Clear(); }
        }
        var hashes = cases.ToDictionary(c => c.Item1, c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(stand, "native-" + c.Item1 + ".wav")))));
        await File.WriteAllTextAsync(Path.Combine(stand, "native-hashes.json"), JsonSerializer.Serialize(hashes));
        Assert.AreNotEqual(hashes["off"], hashes["one"]); Assert.AreNotEqual(hashes["one"], hashes["two"]);
        Assert.AreEqual(hashes["off"], hashes["off-again"]);
    }

    [TestMethod]
    public async Task AceNativeAdapterStrengthAffectsAudioAndBypassRestoresBase()
    {
        var stand = Environment.GetEnvironmentVariable("LOPATA_LORA_STAND");
        if (string.IsNullOrEmpty(stand)) { Assert.Inconclusive("Opt-in native stand only."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_LORA_MODELS")!;
        var d = MusicLoraCatalog.Find("jay2"); var card = d.Card(root); Directory.CreateDirectory(card.InstallDirectory);
        foreach (var file in card.Files) {
            var dest = Path.Combine(card.InstallDirectory, file.RelativePath);
            if (!File.Exists(dest)) File.Copy(Path.Combine(stand, "downloads", "str8bored--JAY_2", file.RelativePath), dest);
        }
        Assert.IsTrue(await MusicLoraAssets.PrepareAsync(root, [new(d.Id, d.Revision, 1)], false, null, default));
        stand = Path.Combine(stand, "ace-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(stand);
        var expert = MusicAceCatalog.Defaults();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) expert.Values[key] = 0;
        var request = new MusicYueRequest("jay_singing_runs, pop, male singer, warm piano", "[Verse]\nWe follow sparks across the rain\nAnd find our way back home again", 918, 514, 10) { Expert = expert };
        var worker = new MusicAceWorker(); var log = new List<string>(); worker.Log += line => { lock (log) { log.Add(line); File.AppendAllText(Path.Combine(stand, "live.log"), line + "\n"); } };
        var hashes = new Dictionary<string, string>();
        foreach (var (name, strength) in new[] { ("off", 0d), ("one", 1d), ("half", .5), ("off-again", 0d) }) {
            var output = Path.Combine(stand, name + ".wav");
            try {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                await worker.SynthesizeAsync(MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Variation), "",
                    request with { Loras = strength == 0 ? [] : [new(d.Id, d.Revision, strength)] }, Path.Combine(stand, name + ".json"), output, cancel.Token);
                await File.WriteAllTextAsync(output + ".receipt.json", worker.LastRequestReceipt);
                Assert.IsTrue(MusicWaveFile.ReadDuration(output) > TimeSpan.Zero);
                hashes[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(output)));
            } finally { lock (log) { File.WriteAllLines(Path.Combine(stand, name + ".log"), log); log.Clear(); } }
        }
        await File.WriteAllTextAsync(Path.Combine(stand, "hashes.json"), JsonSerializer.Serialize(hashes));
        Assert.AreNotEqual(hashes["off"], hashes["one"]); Assert.AreNotEqual(hashes["one"], hashes["half"]);
        Assert.AreEqual(hashes["off"], hashes["off-again"]);
    }
}
