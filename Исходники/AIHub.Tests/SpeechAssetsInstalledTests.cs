using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SpeechAssetsInstalledTests
{
    [TestMethod]
    public async Task InstalledModelCreatesSeparateRuEnTakesAndZip()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_INSTALLED") != "1") Assert.Inconclusive("Opt in to a technical installed-model probe.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "installed");
        var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var library = new ManagedModelLibraryStore(); var card = library.Load(VideoNarratorCatalog.ArtifactId("qwen3-17b")); Assert.IsNotNull(card);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var prepare = new VideoModelPreparation(library);
        Assert.IsTrue(await prepare.PrepareAsync(card.ModelsRoot, new("speech", ["qwen3-17b"]), false, null, timeout.Token), "No downloads in this technical probe.");
        var results = new List<object>();
        foreach (var (language, text) in new[] { ("ru", "Проверяем создание отдельного речевого ассета."), ("en", "Testing a separate speech asset.") })
        {
            var session = new SpeechAssetSession { Voice = new("qwen3-17b", language, 100, new("Ryan")),
                SourceName = language + ".md", Rows = SpeechAssetMarkdown.Parse("- " + text) };
            store.Save(session); var row = session.Rows.Single();
            await new SpeechAssetWork(store).RunAsync(new(session.Id, session.Voice, [new(row.Id, Guid.NewGuid().ToString("N"), row.Text)]), "Technical probe", null, timeout.Token);
            var generated = store.Load(session.Id); var take = generated.Rows[0].Takes.Single();
            Assert.IsFalse(generated.Rows[0].Confirmed);
            using var reader = new WaveFileReader(store.FileFor(session.Id, take.File)); Assert.IsTrue(reader.TotalTime.TotalSeconds > .3);
            Assert.IsTrue(take.Models.Any(m => m.Revision == card.Revision));
            generated.Rows[0].Confirm(); store.Save(generated);
            var zip = Path.Combine(root, language + "-technical-probe.zip"); store.Export(generated, zip);
            using var archive = ZipFile.OpenRead(zip); Assert.HasCount(2, archive.Entries);
            results.Add(new { language, take.Seconds, take.Voice, take.Models, zip });
        }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
