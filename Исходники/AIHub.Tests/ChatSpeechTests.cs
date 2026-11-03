using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ChatSpeechTests
{
    [TestMethod]
    public void ProfilesAreSeparatePersistentAndAutomaticReadingDefaultsOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!.ChatSpeech;
        Assert.IsFalse(settings.AutoRead); Assert.AreEqual("cpu", settings.Choice().Options.Device);
        settings.Apply(new("supertonic3", "ru", 110, new("F2", Device: "cpu", Steps: 12)));
        settings.Apply(new("qwen3-17b", "en", 100, new("Aiden", "Speak calmly", "auto")));
        var copy = JsonSerializer.Deserialize<ChatSpeechSettings>(JsonSerializer.Serialize(settings))!;
        copy.Selected = "supertonic3"; Assert.AreEqual("F2", copy.Choice().Options.Speaker);
        Assert.AreEqual(110, copy.Choice().Rate); Assert.AreEqual(12, copy.Choice().Options.Steps);
        copy.Selected = "qwen3-17b"; Assert.AreEqual("Speak calmly", copy.Choice().Options.Style);
        Assert.ThrowsExactly<InvalidDataException>(() => settings.Apply(new("kokoro-heart", "ru", 100, new("af_heart"))));
    }

    [TestMethod]
    public void CatalogTestSentencesAndLongUnicodeTextHaveNoSilentTruncation()
    {
        foreach (var model in VideoNarratorCatalog.Models)
            foreach (var language in model.Languages) Assert.IsTrue(ChatSpeechText.TestPhrase(language).Length > 10);
        Assert.IsTrue(ChatSpeechText.TestPhrase("ru").StartsWith("Привет"));
        Assert.IsTrue(ChatSpeechText.TestPhrase("en").StartsWith("Hello"));
        var source = string.Concat(Enumerable.Repeat("Большое сообщение 🙂 без потери смысла. ", 200));
        var parts = ChatSpeechText.Parts(source);
        Assert.IsTrue(parts.Count > 5); Assert.IsTrue(parts.All(p => p.Length <= 600));
        Assert.AreEqual(string.Concat(source.Where(c => !char.IsWhiteSpace(c))), string.Concat(string.Join(" ", parts).Where(c => !char.IsWhiteSpace(c))));
        Assert.AreEqual("Поговорим о работе. Пример", ChatSpeechText.Plain("**Поговорим** о [работе](https://example.com). `Пример`"));
    }

    [TestMethod]
    public async Task PartsSurvivePauseResumeAndCacheTracksParametersAndCorruption()
    {
        using var f = new SpeechFixture();
        var request = f.Request(string.Join(" ", Enumerable.Repeat("Длинная проверка без обрезания.", 27)));
        var work = f.Work.GenerateAsync(request, default);
        await f.SecondPart.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await f.Controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(BackgroundOperationPhase.Paused, f.Controller.State!.Phase);
        Assert.IsTrue(f.Controller.State.Private);
        Assert.AreEqual(1, f.Controller.State.Checkpoint!.Value.GetProperty("CompletedParts").GetInt32());
        f.Block = false; await f.Controller.ResumeAsync(default);
        var audio = await work.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(2, audio.Files.Length); Assert.AreEqual(3, f.Calls, "The completed first part must not be synthesized again after resume.");
        foreach (var file in audio.Files)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.IsTrue(Encoding.UTF8.GetString(bytes).Contains("chat/let-it-out/speech"));
            Assert.IsTrue(Encoding.UTF8.GetString(bytes).Contains("LOPATA_PROVENANCE"));
        }
        var repeated = await f.Work.GenerateAsync(request with { Id = Guid.NewGuid().ToString("N") }, default);
        CollectionAssert.AreEqual(audio.Files, repeated.Files); Assert.AreEqual(3, f.Calls);
        await File.WriteAllTextAsync(audio.Files[0], "damaged"); Assert.IsNull(f.Work.Cached(request));
        await f.Work.GenerateAsync(request with { Id = Guid.NewGuid().ToString("N") }, default);
        Assert.AreEqual(4, f.Calls);
        var altered = request with { Id = Guid.NewGuid().ToString("N"), Voice = request.Voice with { Rate = 120 } };
        Assert.IsNull(f.Work.Cached(altered));
        await f.Work.GenerateAsync(altered, default); Assert.AreEqual(6, f.Calls);
        Assert.IsNotNull(f.Work.Cached(request));
        Assert.ThrowsExactly<InvalidDataException>(() => f.Work.Cached(request with { Message = request.Message with { Conversation = "../../escape" } }));
    }

    [TestMethod]
    public async Task SynthesisFailureReleasesChatWithoutRetryAndPreservesCauseAndCompletedParts()
    {
        using var f = new SpeechFixture(); f.Block = false; f.Failure = new IOException("Narrator exited with code -1073741819");
        var request = f.Request(string.Join(" ", Enumerable.Repeat("Длинная проверка без обрезания.", 27)));
        var error = await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => f.Work.GenerateAsync(request, default));
        Assert.AreSame(f.Failure, error.InnerException);
        Assert.IsTrue(ComponentAuditFailures.IsEligible(error), "Unknown native failures must still offer component diagnostics.");
        Assert.AreEqual(2, f.Calls, "A failed worker must not be retried automatically.");
        Assert.IsFalse(f.Controller.BlocksNewWork); Assert.AreEqual(BackgroundOperationPhase.Failed, f.Controller.State!.Phase);
        Assert.AreEqual(1, f.Controller.State.Checkpoint!.Value.GetProperty("CompletedParts").GetInt32());
        var restarted = new BackgroundOperationController(new(Path.Combine(f.Root, "background", "operation.json"))); restarted.Load();
        Assert.IsFalse(restarted.BlocksNewWork); Assert.IsNull(restarted.State, "Finished private jobs must not leave a durable notification/checkpoint.");
        f.Failure = null;
        var result = await f.Work.GenerateAsync(request with { Id = Guid.NewGuid().ToString("N") }, default);
        Assert.AreEqual(3, f.Calls, "An explicit new attempt reuses the completed first part."); Assert.HasCount(2, result.Files);
    }

    [TestMethod]
    public void LegacyRecoveryOnlyFinishesMatchingFailedSpeechAndPreservesCheckpoint()
    {
        using var f = new SpeechFixture(); var request = f.Request("Проверка отказа.");
        foreach (var variant in new[] { "failure", "pause", "interrupted", "other", "mismatch" })
        {
            var store = new BackgroundOperationStore(Path.Combine(f.Root, variant, "operation.json"));
            var state = new BackgroundOperationState { Id = variant == "mismatch" ? Guid.NewGuid().ToString("N") : request.Id,
                Kind = variant == "other" ? "another.kind" : ChatSpeechWork.Kind, Title = "Failed speech", Project = request.Message.Conversation,
                Input = JsonSerializer.SerializeToElement(request), Private = true, Phase = BackgroundOperationPhase.Waiting,
                RequiresDecision = true, UserPaused = variant == "pause", Detail = variant == "interrupted" ? null : "IOException",
                Checkpoint = JsonSerializer.SerializeToElement(new { CompletedParts = 1 }) };
            store.Save(state); var controller = new BackgroundOperationController(store); controller.Load();
            ChatSpeechWork.RecoverFailedAttempts(controller);
            Assert.AreEqual(variant == "failure" ? BackgroundOperationPhase.Failed : BackgroundOperationPhase.Waiting, controller.State!.Phase, variant);
            Assert.IsTrue(JsonElement.DeepEquals(state.Input, controller.State.Input));
            Assert.IsTrue(JsonElement.DeepEquals(state.Checkpoint!.Value, controller.State.Checkpoint!.Value));
        }
    }

    private sealed class SpeechFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "lopata-chat-speech-" + Guid.NewGuid().ToString("N"));
        internal readonly BackgroundOperationController Controller;
        internal readonly ChatSpeechWork Work;
        internal readonly TaskCompletionSource SecondPart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal bool Block = true;
        internal Exception? Failure;
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        internal SpeechFixture()
        {
            Directory.CreateDirectory(Root); Controller = new(new(Path.Combine(Root, "background", "operation.json")));
            ApplicationBackgroundOperations.Current = Controller;
            var library = new ManagedModelLibraryStore(Path.Combine(Root, "library"));
            var registry = new VideoNarratorRegistry(library); registry.Register("vosk010", () => new Fake(this));
            Work = new(library, Path.Combine(Root, "audio"), registry);
        }
        internal ChatSpeechRequest Request(string text) => new(Guid.NewGuid().ToString("N"),
            new(Guid.NewGuid().ToString("N"), ChatSpeechWork.Hash(text), text), new("vosk010", "ru", 100, new("0", Device: "cpu")));
        public void Dispose() { ApplicationBackgroundOperations.Current = _previous; Directory.Delete(Root, true); }
        private sealed class Fake(SpeechFixture owner) : IVideoNarrator
        {
            public IReadOnlyList<VideoVoice> Voices => [];
            public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
            {
                var call = ++owner.Calls;
                if (call == 2 && owner.Failure is { } failure) throw failure;
                if (call == 2 && owner.Block) { owner.SecondPart.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                var path = Path.Combine(owner.Root, "raw-" + call + ".wav");
                using (var wave = new WaveFileWriter(path, new WaveFormat(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
                return new(path, new("test-voice", "Test", "revision-test", "speech"));
            }
            public void Dispose() { }
        }
    }

    [TestMethod]
    [DataRow("vosk010")]
    [DataRow("supertonic3")]
    [DataRow("silero56")]
    public async Task InstalledChatSpeechProducesCachedWavUsingTheSelectedProfile(string id)
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_CHAT_SPEECH_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt in to installed-model synthesis."); return; }
        var folder = Path.Combine(output, id); Directory.CreateDirectory(folder);
        var library = new ManagedModelLibraryStore(); var card = library.Load(VideoNarratorCatalog.ArtifactId(id)); Assert.IsNotNull(card);
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new(Path.Combine(folder, "background", "operation.json")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            using var audit = ComponentAudit.CaptureOperation();
            using var preparation = new VideoModelPreparation(library);
            Assert.IsTrue(await preparation.PrepareAsync(card.ModelsRoot, new("speech", [id]), false, null, timeout.Token), "The probe may not download missing files.");
            var model = VideoNarratorCatalog.Get(id);
            var device = Environment.GetEnvironmentVariable("LOPATA_CHAT_SPEECH_DEVICE") ?? "cpu";
            var speaker = Environment.GetEnvironmentVariable("LOPATA_CHAT_SPEECH_SPEAKER") ?? model.DefaultSpeaker;
            var choice = new VideoNarratorChoice(id, "ru", 100, new(speaker, Device: device));
            var text = Environment.GetEnvironmentVariable("LOPATA_CHAT_SPEECH_TEXT") ?? "Проверяем озвучку чата. Можно спокойно продолжать разговор.";
            var request = new ChatSpeechRequest(Guid.NewGuid().ToString("N"), new(Guid.NewGuid().ToString("N"), ChatSpeechWork.Hash(text), text), choice);
            var worker = new ChatSpeechWork(library, Path.Combine(folder, "audio")); var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = await worker.GenerateAsync(request, timeout.Token);
            var firstSeconds = timer.Elapsed.TotalSeconds; timer.Restart();
            CollectionAssert.AreEqual(result.Files, (await worker.GenerateAsync(request with { Id = Guid.NewGuid().ToString("N") }, timeout.Token)).Files);
            using var reader = new WaveFileReader(result.Files[0]);
            var bytes = await File.ReadAllBytesAsync(result.Files[0], timeout.Token);
            Assert.IsTrue(Encoding.UTF8.GetString(bytes).Contains("chat/let-it-out/speech"));
            Assert.IsTrue(reader.TotalTime.TotalSeconds > 0.5);
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { id, firstSeconds, cachedSeconds = timer.Elapsed.TotalSeconds,
                seconds = reader.TotalTime.TotalSeconds, choice, result }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
}
