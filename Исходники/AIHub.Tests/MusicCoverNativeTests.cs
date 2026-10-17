using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task InstalledCoreAndZImageCreateFourCaptionedCovers()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_COVER_NATIVE") != "1") Assert.Inconclusive("Opt-in native cover generation.");
        var root = Environment.GetEnvironmentVariable("LOPATA_COVER_EVIDENCE") ?? throw new InvalidOperationException("A private evidence directory is required.");
        Directory.CreateDirectory(root);
        var storage = new StorageSettingsStore().LoadOrCreate();
        var core = MusicPoetryModelCatalog.Choices(storage).Single(c => c.Candidate.Id == "core").Model
            ?? throw new InvalidOperationException("Core is not installed.");
        var store = new MusicCoverStore(root); var id = Guid.NewGuid().ToString("N");
        var input = new MusicCoverInput(id, "ЛОПАТА", "Ночной кот",
            "[Verse]\nУ окна уснул мой кот,\nЗвёзды смотрят в тёмный двор.\nТихий джаз из комнат льёт,\nНочь ведёт со мной разговор.",
            "Dreamy nocturnal jazz, calm, warm and playful", storage.Models.Locations.First().Path, core.Path);
        var original = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(root, "background.json")));
        var watch = Stopwatch.StartNew();
        try
        {
            store.Create(input);
            var context = new UserContextService(new UserProfileStore(), new IpLocationService());
            var work = new MusicCoverWork(store, () => new MusicRhymeModel(context,
                reply => File.WriteAllText(Path.Combine(root, "summary-response.json"), reply)));
            var result = await work.RunAsync(input, stage => File.AppendAllText(Path.Combine(root, "events.jsonl"),
                JsonSerializer.Serialize(new { Seconds = watch.Elapsed.TotalSeconds, stage.Stage, Completed = stage.Ready.Length }) + "\n"), "Native song covers", default);
            Assert.HasCount(4, result.Ready); Assert.IsFalse(string.IsNullOrWhiteSpace(result.Summary));
            var selected = store.Confirm(input, result.Ready[0]); _ = MusicCoverStore.ReadArtwork(selected);
            await File.WriteAllTextAsync(Path.Combine(root, "native-result.json"), JsonSerializer.Serialize(new {
                Seconds = watch.Elapsed.TotalSeconds, result.Summary, result.Prompt, result.Ready, Selected = selected,
                Sizes = result.Ready.Select(path => new { Path = path, Bytes = new FileInfo(path).Length }) }));
        }
        finally { ApplicationBackgroundOperations.Current = original; await ApplicationBackgroundOperations.RetireModelsAsync(); }
    }
}
