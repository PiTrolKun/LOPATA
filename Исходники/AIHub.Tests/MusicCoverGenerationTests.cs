using System.IO;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverGenerationTests
{
    [TestMethod]
    public void IdentityAndManualOffPersistWithoutStrippingRealDigits()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var store = new MusicCoverStore(root);
            Assert.IsTrue(store.Read("Artist 01", "Song 02").IsEnabled);
            Assert.IsFalse(store.Read("", "Song").IsEnabled);
            store.SetEnabled("Artist 01", "Song 02", false);
            Assert.IsFalse(new MusicCoverStore(root).Read(" Artist 01 ", "Song 02").IsEnabled);
            Assert.AreNotEqual(MusicCoverStore.Key("Artist 01", "Song 02"), MusicCoverStore.Key("Artist 01", "Song 03"));
            Assert.IsTrue(store.Read("Artist 01", "Song 03").IsEnabled);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task FreshSummaryFourImagesCaptionAndManualRegenerationKeepPreviousChoice()
    {
        var root = MusicOutputTests.Temporary(); var original = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(root, "background.json")));
        try
        {
            var store = new MusicCoverStore(root); var model = new SummaryModel(); var worker = new ImageWorker();
            var work = new MusicCoverWork(store, () => model, new(worker), (_, _) => Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([]));
            var input = Input(); store.Create(input);
            var ready = await work.RunAsync(input, _ => { }, "Covers", default);
            Assert.AreEqual(1, model.Calls); Assert.AreEqual(4, worker.Calls.Count);
            Assert.AreEqual("Ready", ready.Stage); Assert.HasCount(4, ready.Ready);
            Assert.IsFalse(ready.Prompt.Contains(input.Lyrics));
            StringAssert.Contains(ready.Prompt, model.Summary);
            foreach (var path in ready.Ready) { using var image = SKBitmap.Decode(path); Assert.AreEqual(1024, image.Width); Assert.AreEqual(1024, image.Height); }
            var chosen = store.Confirm(input, ready.Ready[0]);
            var next = input with { Id = Guid.NewGuid().ToString("N"), PromptOverride = "A blue room with a cat, no text" }; store.Create(next);
            var another = await work.RunAsync(next, _ => { }, "Covers", default);
            Assert.AreEqual(1, model.Calls, "Edited descriptions bypass the model and stay literal.");
            Assert.AreEqual(next.PromptOverride, another.Images!.Prompt);
            Assert.AreEqual(chosen, store.Read(input.Artist, input.Title).Selected);
            Assert.Throws<InvalidDataException>(() => store.Confirm(input, another.Ready[0]));
            store.Confirm(next, another.Ready[1]);
            Assert.AreNotEqual(chosen, store.Read(input.Artist, input.Title).Selected);
            var count = worker.Calls.Count;
            await work.RunAsync(next, _ => { }, "Covers", default);
            Assert.AreEqual(count, worker.Calls.Count, "Completed image slots must not be regenerated.");
        }
        finally { ApplicationBackgroundOperations.Current = original; Cleanup(root); }
    }

    [TestMethod]
    public async Task CancellationAndRestartReuseSummaryAndCompletedImageSlots()
    {
        var root = MusicOutputTests.Temporary(); var original = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(root, "background.json")));
        try
        {
            var store = new MusicCoverStore(root); var input = Input(); store.Create(input);
            var model = new SummaryModel(); using var cancellation = new CancellationTokenSource();
            var worker = new ImageWorker { Before = index => { if (index == 2) cancellation.Cancel(); } };
            var work = new MusicCoverWork(store, () => model, new(worker), (_, _) => Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([]));
            await Assert.ThrowsAsync<OperationCanceledException>(() => work.RunAsync(input, _ => { }, "Covers", cancellation.Token));
            var stopped = store.Load(input); Assert.AreEqual(model.Summary, stopped.Summary);
            Assert.HasCount(2, ImageGenerationSessionStore.Load(stopped.Images!.SessionDirectory).Turns[0].Results);
            worker.Before = null;
            var completed = await work.RunAsync(input, _ => { }, "Covers", default);
            Assert.AreEqual(1, model.Calls); Assert.HasCount(4, completed.Ready);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 2, 3 }, worker.Calls.ToArray());
            Assert.IsNull(completed.Error);
        }
        finally { ApplicationBackgroundOperations.Current = original; Cleanup(root); }
    }

    [TestMethod]
    [DataRow("LOPATA", "Cat song")]
    [DataRow("Исполнитель 01", "Название 02 с буквами ё и ударением о́")]
    [DataRow("", "Название")]
    public void CaptionLayoutReservesNativeDimensionsAndKeepsFinalSquare(string artist, string title)
    {
        var layout = MusicCoverLayout.Create(artist, title);
        Assert.AreEqual(0, layout.ImageHeight % 64); Assert.AreEqual(1024, layout.ImageHeight + layout.StripHeight);
        Assert.IsTrue(layout.StripHeight >= 128); Assert.IsTrue(layout.Lines.Length > 0);
        var longLayout = MusicCoverLayout.Create(new string('Я', 160), new string('ё', 160));
        Assert.IsTrue(longLayout.StripHeight <= 384); Assert.IsTrue(longLayout.Lines.All(line => line.Length > 0));
        var caption = string.Concat(longLayout.Lines).Replace(" ", "");
        Assert.AreEqual(new string('Я', 160) + "—" + new string('ё', 160), caption);
    }

    [TestMethod]
    [DataRow(MusicAudioFormat.Mp3)]
    [DataRow(MusicAudioFormat.Opus)]
    [DataRow(MusicAudioFormat.Flac)]
    [DataRow(MusicAudioFormat.Wav)]
    public async Task ChosenArtworkReplacesDefaultWithoutChangingAudioOrOtherTags(MusicAudioFormat format)
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var source = Path.Combine(root, "source.wav"); MusicOutputTests.WriteWave(source);
            var encoded = Path.Combine(root, "song" + MusicOutputSettings.Extension(format));
            await MusicAudioEncoder.Default.EncodeAsync(source, encoded, format, 160,
                new Dictionary<string, string> { ["title"] = "Title ё", ["LOPATA_UNKNOWN"] = "a = ; #\nSecond line",
                    [MusicCoverProvenance.ModelTag] = "Previous image model", [MusicCoverProvenance.SourceTag] = "standard" }, default);
            var payload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(encoded, default);
            var timing = new MusicTimedLyrics { JobId = Guid.NewGuid().ToString("N"), Variant = 0, SourceLyrics = "Припев",
                LyricsHash = MusicTimedLyrics.HashLyrics("Припев"), AudioPayloadHash = payload, DurationSeconds = 1,
                Provider = "test", ProviderVersion = "1", Lines = [new(0, 6, .1, .9)] };
            var timed = Path.Combine(root, "timed" + MusicOutputSettings.Extension(format));
            await MusicLyricsTimingMetadata.WriteCopyAsync(encoded, timed, timing, default);
            var choice = Fixture(root); var cover = MusicCoverStore.ReadArtwork(choice);
            var changed = Path.Combine(root, "changed" + MusicOutputSettings.Extension(format));
            await MusicCoverMetadata.WriteCopyAsync(timed, changed, cover, default, MusicCoverProvenance.Tags(choice));
            var info = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, changed, default);
            Assert.HasCount(1, info.Covers); CollectionAssert.AreEqual(cover.Data, info.Covers[0].Data);
            Assert.AreEqual(payload, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(changed, default));
            Assert.IsNotNull(await MusicLyricsTimingMetadata.ReadAsync(changed, default));
            Assert.AreEqual("Title ё", info.Tags["title"]); Assert.AreEqual("a = ; #\nSecond line", info.Tags["LOPATA_UNKNOWN"]);
            Assert.AreEqual("Z-Image Turbo", info.Tags[MusicCoverProvenance.ModelTag]);
            Assert.AreEqual("generated", info.Tags[MusicCoverProvenance.SourceTag]);
            var subsequent = Path.Combine(root, "subsequent" + MusicOutputSettings.Extension(format));
            await MusicAudioEncoder.Default.EncodeAsync(source, subsequent, format, 160, new Dictionary<string, string>(), choice, default);
            var next = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, subsequent, default);
            CollectionAssert.AreEqual(cover.Data, next.Covers.Single().Data);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LateArtworkUpdatesBothFileReceiptsAndSurvivesPlayerLockRecovery()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs")); var settings = new MusicExpertSettings(); settings.Values["cot"] = 2;
            var job = jobs.Create(root, Path.Combine(root, "out"), "Title", 1, 30, "", "", expert: settings,
                outputSettings: new() { Format = MusicAudioFormat.Opus, AdditionalFormat = MusicAudioFormat.Mp3 });
            await new MusicGenerationRunner(jobs, new AudioWorker()).RunAsync(job.Id, default);
            var variant = jobs.Load(job.Id).Variants[0]; var choice = Fixture(root) with { ModelName = "A different image model" };
            var originalMetadata = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, variant.ResultPath, default);
            Assert.AreEqual("Z-Image Turbo", originalMetadata.Tags[MusicCoverProvenance.ModelTag]);
            Assert.AreEqual("standard", originalMetadata.Tags[MusicCoverProvenance.SourceTag]);
            using (var playerLock = new FileStream(variant.ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(await MusicCoverMetadata.ApplyAsync(jobs, job.Id, choice, default));
                Assert.AreEqual(variant.ResultHash, await MusicOutputPublisher.HashAsync(variant.ResultPath, default));
                Assert.IsTrue(Directory.GetFiles(jobs.Folder(job.Id), "*.transaction.json").Length > 0);
            }
            var recovered = await MusicOutputPublisher.VerifyCompletedAsync(jobs, job.Id, 0, default);
            Assert.AreNotEqual(variant.ResultHash, recovered.Variants[0].ResultHash);
            Assert.AreNotEqual(variant.AdditionalHash, recovered.Variants[0].AdditionalHash);
            var cover = MusicCoverStore.ReadArtwork(choice);
            foreach (var path in new[] { recovered.Variants[0].ResultPath, recovered.Variants[0].AdditionalPath! })
            {
                await MusicCoverArt.VerifyAsync(MusicAudioRuntime.Default, path, default, cover);
                var metadata = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, path, default);
                Assert.AreEqual(choice.ModelName, metadata.Tags[MusicCoverProvenance.ModelTag]);
                Assert.AreEqual("generated", metadata.Tags[MusicCoverProvenance.SourceTag]);
                Assert.AreEqual(originalMetadata.Tags["LOPATA_MODEL"], metadata.Tags["LOPATA_MODEL"]);
            }
            Assert.AreEqual(0, Directory.GetFiles(jobs.Folder(job.Id), "*.transaction.json").Length);
        }
        finally { Cleanup(root); }
    }
    internal static MusicCoverSelection Fixture(string root)
    {
        var input = Input(); var store = new MusicCoverStore(Path.Combine(root, "covers")); store.Create(input);
        var path = Path.Combine(store.RunFolder(input), "cover-0.jpg");
        using var image = SKSurface.Create(new SKImageInfo(1024, 1024)); image.Canvas.Clear(SKColors.CornflowerBlue);
        using var snapshot = image.Snapshot(); using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, jpeg.ToArray()); store.SaveRun(store.Load(input) with { Ready = [path], Stage = "Ready" });
        return store.Confirm(input, path);
    }
    private static MusicCoverInput Input() => new(Guid.NewGuid().ToString("N"), "LOPATA", "Cat 01", "СЕКРЕТНЫЙ ОРИГИНАЛ ПЕСНИ", "gentle jazz", "unused", "unused.gguf");
    private static void Cleanup(string root)
    {
        foreach (var failure in Directory.GetFiles(root, "failure.log", SearchOption.AllDirectories)) Console.WriteLine(File.ReadAllText(failure));
        Directory.Delete(root, true);
    }
    internal sealed class SummaryModel : IMusicRhymeModel
    {
        public string Summary => "A lonely cat watches stars outside a warm music workshop.";
        public int Calls { get; private set; }
        public Task<string> SummarizeAsync(DebugModelInfo model, string prompt, MusicRhymeTuning tuning, CancellationToken token)
        { Calls++; return Task.FromResult(Summary); }
        public Task<string> RewriteAsync(DebugModelInfo model, string prompt, CancellationToken token) => throw new InvalidOperationException("No editing/chat/RAG request is allowed.");
        public void Dispose() { }
    }
    internal sealed class ImageWorker : IImageGenerationWorker
    {
        public List<int> Calls { get; } = [];
        public Action<int>? Before { get; set; }
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            Calls.Add(index); Before?.Invoke(index); token.ThrowIfCancellationRequested();
            using var surface = SKSurface.Create(new SKImageInfo(request.Width, request.Height)); surface.Canvas.Clear(new SKColor((byte)(index * 60), 100, 200));
            using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(output, png.ToArray()); return Task.CompletedTask;
        }
    }
    private sealed class AudioWorker : IMusicYueWorker
    {
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) => throw new InvalidOperationException();
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
}
