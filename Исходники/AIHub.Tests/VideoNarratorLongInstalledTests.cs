using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoNarratorLongInstalledTests
{
    [TestMethod]
    [DataRow("kokoro-sveta")][DataRow("kokoro-heart")][DataRow("kitten2")][DataRow("vosk010")]
    [DataRow("supertonic3")][DataRow("qwen3-17b")][DataRow("qwen3-06b")][DataRow("orpheus3b")]
    [DataRow("silero56")][DataRow("cosyvoice300")][DataRow("kitten2-cpu")]
    public async Task EveryPreparedNarratorProducesOneFileFromMultipleNativeCalls(string variant)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_EVIDENCE");
        var selected = Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_MODELS");
        if (string.IsNullOrWhiteSpace(root) || selected?.Split(',').Contains(variant) != true)
        { Assert.Inconclusive("Explicit installed-model invocation required."); return; }
        Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_AUDIT") ?? Path.Combine(root, "audit"), false);
        using var operation = ComponentAudit.CaptureOperation();
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            var id = variant.Replace("-cpu", ""); var model = VideoNarratorCatalog.Get(id);
            var language = model.Languages.Contains("ru") ? "ru" : "en";
            var text = language == "ru" ? "Это первая тестовая часть.\n\nЭто вторая тестовая часть." : "This is the first test section.\n\nThis is the second test section.";
            using var narrator = new VideoNarratorRegistry().Create(id);
            var progress = new List<double>(); ((IVideoNarratorProgress)narrator).Progress += progress.Add;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var audio = await ((IConfiguredVideoNarrator)narrator).GenerateAsync(id, language, text, 100,
                new(model.DefaultSpeaker, Device: variant.EndsWith("-cpu", StringComparison.Ordinal) ? "cpu" : "gpu"), deadline.Token);
            using var facts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(audio.Path)!, "execution.json")));
            Assert.AreEqual(2, facts.RootElement.GetProperty("actual").GetProperty("chunking").GetProperty("packets").GetInt32());
            Assert.IsGreaterThanOrEqualTo(2, progress.Count); Assert.AreEqual(1, progress.Last());
            using var wave = new NAudio.Wave.WaveFileReader(audio.Path); Assert.IsGreaterThan(.1, wave.TotalTime.TotalSeconds);
            File.Copy(audio.Path, Path.Combine(root, variant + ".wav"), true);
            File.Copy(Path.Combine(Path.GetDirectoryName(audio.Path)!, "chunks.jsonl"), Path.Combine(root, variant + "-chunks.jsonl"), true);
            await File.WriteAllTextAsync(Path.Combine(root, variant + ".json"), JsonSerializer.Serialize(new {
                id, variant, text, audio.Models, seconds = wave.TotalTime.TotalSeconds, progress, execution = facts.RootElement.Clone()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
}
