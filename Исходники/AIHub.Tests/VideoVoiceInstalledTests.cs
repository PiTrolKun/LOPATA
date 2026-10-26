using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceInstalledTests
{
    [TestMethod]
    [DataRow("supertonic3")]
    [DataRow("kitten2")]
    [DataRow("qwen3-06b")]
    [DataRow("qwen3-17b")]
    [DataRow("vosk010")]
    [DataRow("silero56")]
    [DataRow("orpheus3b")]
    [DataRow("cosyvoice300")]
    [DataRow("kokoro-sveta")]
    [DataRow("kokoro-heart")]
    public async Task ExplicitInstalledNarratorProducesPcmWithActualProvenance(string id)
    {
        var requested = Environment.GetEnvironmentVariable("LOPATA_VOICE_SMOKE");
        var modelsRoot = Environment.GetEnvironmentVariable("LOPATA_VOICE_MODELS_ROOT");
        var resultsRoot = Environment.GetEnvironmentVariable("LOPATA_VOICE_SMOKE_OUTPUT");
        if (requested?.Split(',').Contains(id) != true || string.IsNullOrWhiteSpace(modelsRoot) || string.IsNullOrWhiteSpace(resultsRoot))
        { Assert.Inconclusive("Explicit model download/inference authorization and paths are required."); return; }
        var root = Path.Combine(resultsRoot, id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(Path.Combine(library, "Entries"));
        foreach (var file in Directory.EnumerateFiles(AppDataPaths.ManagedModelLibraryEntriesDirectory, "*.json"))
            File.Copy(file, Path.Combine(library, "Entries", Path.GetFileName(file)));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var store = new ManagedModelLibraryStore(library);
            var seed = Environment.GetEnvironmentVariable("LOPATA_VOICE_SMOKE_SEED");
            if (!string.IsNullOrWhiteSpace(seed))
                foreach (var card in VideoModelPreparation.Cards(modelsRoot, new("speech", [id])))
                    foreach (var file in card.Files)
                    {
                        var source = RuntimeFileAudit.Contained(seed, file.RelativePath);
                        var target = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
                        Lopata.Updates.SafeUpdatePath.RejectLinks(source); Lopata.Updates.SafeUpdatePath.RejectLinks(target);
                        if (!File.Exists(source) || File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
                    }
            using var prepare = new VideoModelPreparation(store);
            Assert.IsTrue(await prepare.PrepareAsync(modelsRoot, new("speech", [id]), true, null, deadline.Token));
            var model = VideoNarratorCatalog.Get(id); var language = model.Languages.Contains("ru") ? "ru" : "en";
            var options = new VideoVoiceOptions(model.DefaultSpeaker);
            using var narrator = new VideoNarratorRegistry(store).Create(id);
            var text = language == "ru" ? "Привет, мир." : "Hello, world.";
            var audio = narrator is IConfiguredVideoNarrator configured
                ? await configured.GenerateAsync(id, language, text, 100, options, deadline.Token)
                : await narrator.GenerateAsync(id, language, text, 100, deadline.Token);
            File.Copy(audio.Path, Path.Combine(root, "narration.wav"));
            using var wave = new NAudio.Wave.WaveFileReader(audio.Path);
            Assert.IsTrue(wave.Length > 0); Assert.IsTrue(wave.TotalTime.TotalSeconds > 0);
            Assert.AreEqual(16, wave.WaveFormat.BitsPerSample);
            var buffer = new byte[4096]; double peak = 0; int read;
            while ((read = wave.Read(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i + 1 < read; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
            Assert.IsTrue(peak > 0.001, "The probe must contain actual non-silent audio.");
            var payload = await File.ReadAllBytesAsync(audio.Path, deadline.Token);
            Assert.IsTrue(payload.AsSpan().IndexOf("LOPATA_PROVENANCE"u8) >= 0, "Every exposed trial, including Kokoro, must embed its provenance.");
            Assert.IsTrue(audio.Models.Any(m => m.Id == VideoNarratorCatalog.ArtifactId(id)));
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new {
                id, success = true, elapsedSeconds = timer.Elapsed.TotalSeconds, seconds = wave.TotalTime.TotalSeconds,
                rate = wave.WaveFormat.SampleRate, bits = wave.WaveFormat.BitsPerSample, peak, audio.Models
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (Environment.GetEnvironmentVariable("LOPATA_VOICE_SMOKE_REGISTER") == "1")
            {
                // Explicit maintenance invocation only, after backing up the user's library entries.
                var live = new ManagedModelLibraryStore();
                using var acquisition = new ManagedModelAcquisitionService(live);
                foreach (var card in VideoModelPreparation.Cards(modelsRoot, new("speech", [id])))
                {
                    live.Upsert(store.Load(card.ModelArtifactId) ?? throw new InvalidDataException("Missing verified smoke-test card."));
                    // Upsert intentionally invalidates status when moving from an older revision's directory.
                    var checkedCard = await acquisition.VerifyAsync(card.ModelArtifactId, null, deadline.Token);
                    Assert.AreEqual(AIHub.Models.ManagedModelStatuses.Installed, checkedCard.Status);
                }
            }
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "failure.txt"), error.ToString()); throw;
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
