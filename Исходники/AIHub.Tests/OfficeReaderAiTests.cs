using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeReaderAiTests
{
    [TestMethod]
    public void DefaultPreferencesAreCoreAndMultilingualQwenWithoutWritingAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "reader.json");
        var value = new ReaderAiSettingsStore(path).Load();
        Assert.AreEqual(ManagedModelCatalog.CoreArtifactId, value.TextModel);
        Assert.AreEqual("qwen3-17b", value.Speech.Choice().Id);
        Assert.AreEqual("Ryan", value.Speech.Choice().Options.Speaker);
        Assert.IsFalse(File.Exists(path));
    }
    [TestMethod]
    public void ReaderPreferencesRoundTripAndRejectInvalidVoiceWithoutReplacingSavedFile()
    {
        var root = NewRoot(); var path = Path.Combine(root, "reader.json"); var store = new ReaderAiSettingsStore(path);
        try
        {
            var value = store.Load(); value.TextModel = "future-executor";
            value.Speech.Apply(new("silero56", "ru", 100, new(Speaker: "baya", Device: "cpu"))); store.Save(value);
            Assert.AreEqual("future-executor", store.Load().TextModel);
            Assert.AreEqual("baya", store.Load().Speech.Choice().Options.Speaker);
            var before = File.ReadAllText(path); value.Speech.Selected = "missing";
            Assert.Throws<InvalidDataException>(() => store.Save(value));
            Assert.AreEqual(before, File.ReadAllText(path));
            Assert.HasCount(1, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void DamagedOptionalPreferencesRecoverToUsableDefaults()
    {
        var root = NewRoot(); var path = Path.Combine(root, "reader.json");
        try
        {
            File.WriteAllText(path, "{\"Speech\":{\"Selected\":\"missing\",\"Profiles\":{\"bad\":null}}}");
            Assert.AreEqual("Ryan", new ReaderAiSettingsStore(path).Load().Speech.Choice().Options.Speaker);
            File.WriteAllText(path, "broken");
            Assert.AreEqual(ManagedModelCatalog.CoreArtifactId, new ReaderAiSettingsStore(path).Load().TextModel);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void TextCatalogUsesCommonInventoryAndExcludesVoiceProjectorAndEmbeddingWeights()
    {
        var future = Card("future", "An unknown future model");
        var embedding = Card("embedding", "Search encoder"); embedding.Role = ManagedModelRoles.Embedding;
        var projector = Card("mmproj", "Projector");
        var speech = Card("speech", "Speech"); speech.Role = ManagedModelRoles.Speech;
        var cards = ReaderAiModelCatalog.TextModels(new(), [future, embedding, projector, speech]);
        Assert.AreEqual(ManagedModelCatalog.CoreArtifactId, cards[0].Id);
        Assert.IsTrue(cards.Any(c => c.Id == "future" && c.Experimental));
        Assert.IsFalse(cards.Any(c => c.Id is "embedding" or "mmproj" or "speech"));
        Assert.IsTrue(cards.Any(c => c.Card.Architecture.Contains("gemma", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(cards.Any(c => c.Id == ManagedModelCatalog.Qwen25OmniHeavyArtifactId));
        Assert.IsFalse(cards.Any(c => c.Id == ManagedModelCatalog.FlorenceLargeArtifactId));
        Assert.IsFalse(cards.Any(c => c.Card.Family is "ImageGeneration" or "YuE2"));
        Assert.IsTrue(cards.Any(c => c.Id == ManagedModelCatalog.KimiMediumArtifactId));
    }
    [TestMethod]
    public void OnDiskFilesDetermineOrderAndStaleInstalledFlagsDoNot()
    {
        var root = NewRoot();
        try
        {
            var present = Card("present", "Z present"); present.InstallDirectory = root;
            File.WriteAllBytes(Path.Combine(root, present.Files[0].RelativePath), [1, 2]);
            var stale = Card("stale", "A stale"); stale.InstallDirectory = root; stale.Status = ManagedModelStatuses.Installed;
            var models = ReaderAiModelCatalog.TextModels(new(), [stale, present]);
            Assert.AreEqual("present", models[1].Id);
            Assert.IsTrue(models[1].Downloaded);
            Assert.IsFalse(models.Single(c => c.Id == "stale").Downloaded);
            File.AppendAllText(Path.Combine(root, present.Files[0].RelativePath), "extra");
            Assert.IsFalse(ReaderAiModelCatalog.Downloaded(present));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ExternalGgufReadsArchitectureSoAnUnlabelledEncoderCannotBecomeATextModel()
    {
        var root = NewRoot();
        try
        {
            var card = Card("unknown", "New model"); card.Architecture = ""; card.InstallDirectory = root;
            using (var stream = File.Create(Path.Combine(root, card.Files[0].RelativePath)))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(1ul);
                Write(writer, "general.architecture"); writer.Write(8u); Write(writer, "bert");
            }
            Assert.IsFalse(ReaderAiModelCatalog.SupportsText(card));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void AllNarratorsRemainAvailableAndSileroAlwaysLeads()
    {
        var models = ReaderAiModelCatalog.SpeechModels("", []);
        Assert.AreEqual(10, models.Count); Assert.AreEqual("qwen3-17b", models[0].Id);
        Assert.IsTrue(models.All(m => m.Card.TotalBytes > 0));
        Assert.AreEqual("1 GB", ReaderAiModelCatalog.Size(1_000_000_000));
    }
    private static ManagedModelArtifactCard Card(string id, string name) => new()
    {
        ModelArtifactId = id, DisplayName = name, Architecture = "llama", Format = "GGUF", InstallDirectory = Path.Combine(Path.GetTempPath(), id),
        Files = [new() { RelativePath = id + ".gguf", SizeBytes = 2, Purpose = "main_model" }]
    };
    private static void Write(BinaryWriter writer, string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
    private static string NewRoot() { var path = Path.Combine(Path.GetTempPath(), "Reader-ai-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
