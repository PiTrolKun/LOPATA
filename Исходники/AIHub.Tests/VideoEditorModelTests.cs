using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoEditorModelTests
{
    [TestMethod]
    public async Task ExplicitFailedAnalysisProjectNowProducesDraftAcrossTinyLeadingGap()
    {
        var inputPath = Environment.GetEnvironmentVariable("LOPATA_VIDEO_ANALYSIS_REPRO");
        if (string.IsNullOrWhiteSpace(inputPath)) { Assert.Inconclusive("Explicit failed-project model regression required."); return; }
        var root = Path.Combine("H:\\AI_HUB\\Тесты\\VideoPopupAnalysis_20261024", "Model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var originalInput = await File.ReadAllBytesAsync(inputPath); var project = VideoProjectStore.Load(inputPath);
        var segment = project.Narration.OrderBy(n => n.Start).First();
        Assert.IsFalse(project.Tracks.Where(t => t.Kind == "video" && !t.Hidden).SelectMany(t => t.Clips).Any(c => segment.Start >= c.Start && segment.Start < c.Start + c.Duration), "Regression fixture must reproduce the old rejected start");
        var plan = VideoAnalysisFrames.Plan(project, segment); Assert.HasCount(3, plan);
        project.Id = Guid.NewGuid().ToString("N"); var snapshot = Path.Combine(root, "input.lopataVideo"); VideoProjectStore.Save(project, snapshot);
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(Path.Combine(library, "Entries"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDataPaths.ManagedModelLibraryDirectory, "Entries"), "*.json")) File.Copy(file, Path.Combine(library, "Entries", Path.GetFileName(file)));
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await EngineSetService.Shared.InitializeAsync(deadline.Token);
            var work = new VideoWork(new ManagedModelLibraryStore(library)); var result = await work.RunAsync(new(Guid.NewGuid().ToString("N"), snapshot, "analyze", Segments: [segment.Id]), deadline.Token);
            var draft = VideoProjectStore.Load(result).Narration.Single(n => n.Id == segment.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(draft.Text)); Assert.IsTrue(draft.Models.Length > 0);
            CollectionAssert.AreEqual(originalInput, await File.ReadAllBytesAsync(inputPath));
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Draft = draft.Text, Frames = plan, Models = draft.Models, SourceProjectUntouched = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
    [TestMethod]
    public async Task InstalledQwenAndKokoroCreateEditableDraftAndMeasuredSpeech()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_MODEL_TEST") != "1") { Assert.Inconclusive("Explicit local model integration test; normal test runs do not launch models."); return; }
        var root = Path.Combine("H:\\AI_HUB\\Тесты\\VideoEditor_20261024", "Models-" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(root);
        using var policy = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
        var savedGate = ComponentLicenseGate.ConfirmAsync;
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(Path.Combine(library, "Entries"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppDataPaths.ManagedModelLibraryDirectory, "Entries"), "*.json")) File.Copy(file, Path.Combine(library, "Entries", Path.GetFileName(file)));
        var work = new VideoWork(new ManagedModelLibraryStore(library)); work.Changed += progress => File.AppendAllText(Path.Combine(root, "progress.jsonl"), JsonSerializer.Serialize(progress) + "\n");
        try
        {
            // Isolated test acknowledgement callback; the user's receipts and library cards stay unchanged.
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12)); var token = deadline.Token;
            await EngineSetService.Shared.InitializeAsync(token);
            var source = "H:\\AI_HUB\\Тесты\\VideoNarration_20261024\\lopata-svg-navigation-32s.mp4";
            var p = new VideoProject { Width = 960, Height = 540, Fps = 12, Name = "Проверка Qwen и речи" };
            var asset = await VideoImport.ReadAsync(source, root, token); VideoImport.Insert(p, asset, 0);
            p.Tracks[0].Clips[0].Duration = 4; foreach (var t in p.Tracks.Where(t => t.Kind == "audio")) foreach (var c in t.Clips) c.Duration = 4;
            p.Narration.Add(new() { Start = 0, End = 4 });
            var input = Path.Combine(root, "input.lopataVideo"); VideoProjectStore.Save(p, input);
            var draftPath = await work.RunAsync(new(Guid.NewGuid().ToString("N"), input, "analyze", Brief: "Короткое знакомство с интерфейсом программы."), token);
            var draft = VideoProjectStore.Load(draftPath); Assert.IsFalse(string.IsNullOrWhiteSpace(draft.Narration[0].Text)); Assert.IsTrue(draft.Narration[0].Models.Length > 0);
            File.Copy(draftPath, Path.Combine(root, "draft.lopataVideo"));
            var speechPath = await work.RunAsync(new(Guid.NewGuid().ToString("N"), draftPath, "speech"), token);
            var speech = VideoProjectStore.Load(speechPath); var segment = speech.Narration[0]; Assert.IsNotNull(segment.SpokenDuration); Assert.IsTrue(segment.SpokenDuration > 0); Assert.IsFalse(segment.Stale);
            var voice = speech.Asset(segment.AudioAssetId!); Assert.IsTrue(voice.Models.Any(m => m.Role.Contains("speech"))); Assert.IsTrue(File.Exists(voice.Path));
            var output = Path.Combine(root, "narrated.mp4"); await VideoExport.RunAsync(speech, output, new(960, 540, 12, 0, speech.Duration, true, true), Guid.NewGuid().ToString("N"), null, token);
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { text = segment.Text, segment.SpokenDuration, output, models = voice.Models }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally { ComponentLicenseGate.ConfirmAsync = savedGate; }
    }
}
