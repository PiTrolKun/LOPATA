using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class CoreVoiceEngineRouterTests
{
    [TestMethod]
    public async Task SpeakAsync_UsesSavedLegacyFallbackAndRevealsTextWhenVoicesAreUnavailable()
    {
        var espeak = new FakeVoiceEngine(true);
        var rhVoice = new FakeVoiceEngine(true);
        using var router = new CoreVoiceEngineRouter(espeak, rhVoice, new FakeVoiceEngine(false));
        var request = CreateRequest(CoreVoiceSettings.KittenMiniProvider);
        request.Settings.FallbackProvider = CoreVoiceSettings.RhVoiceProvider;
        await router.SpeakAsync(request, new SpeechProgress(), CancellationToken.None);
        Assert.AreEqual(1, rhVoice.SpeakCalls);
        Assert.AreEqual(0, espeak.SpeakCalls);

        using var unavailable = new CoreVoiceEngineRouter(new FakeVoiceEngine(false), new FakeVoiceEngine(false));
        var progress = new SpeechProgress();
        var result = await unavailable.SpeakAsync(request, progress, CancellationToken.None);
        Assert.IsTrue(result.Skipped);
        Assert.IsTrue(progress.Last?.IsComplete);
        Assert.AreEqual(request.Segments[0].Text.Length, progress.Last!.VisibleCharacters["question"]);
    }

    [TestMethod]
    public async Task SpeakAsync_UsesEspeakWhenExplicitlySelected()
    {
        using var espeak = new FakeVoiceEngine(true);
        using var rhVoice = new FakeVoiceEngine(true);
        using var router = new CoreVoiceEngineRouter(espeak, rhVoice);

        await router.SpeakAsync(CreateRequest(CoreVoiceSettings.EspeakProvider), new Progress<CoreSpeechProgress>(), CancellationToken.None);

        Assert.AreEqual(1, espeak.SpeakCalls);
        Assert.AreEqual(0, rhVoice.SpeakCalls);
    }

    [TestMethod]
    public async Task SpeakAsync_UsesMiniByDefaultWhenPrepared()
    {
        var mini = new FakeVoiceEngine(true);
        var espeak = new FakeVoiceEngine(true);
        using var router = new CoreVoiceEngineRouter(espeak, new FakeVoiceEngine(true), mini);
        var settings = new CoreVoiceSettings();

        await router.SpeakAsync(CreateRequest(settings.Provider), new Progress<CoreSpeechProgress>(), CancellationToken.None);

        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, settings.Provider);
        Assert.AreEqual(1, mini.SpeakCalls);
        Assert.AreEqual(0, espeak.SpeakCalls);
    }

    [TestMethod]
    public async Task SpeakAsync_KeepsMiniSelectedWhenComponentsAreMissing()
    {
        var mini = new FakeVoiceEngine(false);
        var espeak = new FakeVoiceEngine(true);
        using var router = new CoreVoiceEngineRouter(espeak, new FakeVoiceEngine(true), mini);
        var request = CreateRequest(CoreVoiceSettings.KittenMiniProvider);

        await router.SpeakAsync(request, new Progress<CoreSpeechProgress>(), CancellationToken.None);

        Assert.AreEqual(1, espeak.SpeakCalls);
        Assert.AreEqual(0, mini.SpeakCalls);
        Assert.AreEqual(CoreVoiceSettings.KittenMiniProvider, request.Settings.Provider);
    }

    [TestMethod]
    public async Task SpeakAsync_UsesRhVoiceWhenSelectedAndAvailable()
    {
        using var espeak = new FakeVoiceEngine(true);
        using var rhVoice = new FakeVoiceEngine(true);
        using var router = new CoreVoiceEngineRouter(espeak, rhVoice);

        await router.SpeakAsync(CreateRequest(CoreVoiceSettings.RhVoiceProvider), new Progress<CoreSpeechProgress>(), CancellationToken.None);

        Assert.AreEqual(0, espeak.SpeakCalls);
        Assert.AreEqual(1, rhVoice.SpeakCalls);
    }

    [TestMethod]
    public async Task SpeakAsync_FallsBackToEspeakWhenRhVoiceIsUnavailable()
    {
        using var espeak = new FakeVoiceEngine(true);
        using var rhVoice = new FakeVoiceEngine(false);
        using var router = new CoreVoiceEngineRouter(espeak, rhVoice);

        await router.SpeakAsync(CreateRequest(CoreVoiceSettings.RhVoiceProvider), new Progress<CoreSpeechProgress>(), CancellationToken.None);

        Assert.AreEqual(1, espeak.SpeakCalls);
        Assert.AreEqual(0, rhVoice.SpeakCalls);
    }

    [TestMethod]
    public async Task InstalledMiniSynthesizesAndPlaysThroughCoreVoiceRouter()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CORE_MINI_SMOKE") != "1")
        {
            Assert.Inconclusive("Requires the installed Mini and Windows audio output.");
            return;
        }

        using var coordinator = new CoreSpeechPresentationCoordinator(CoreVoiceEngineRouter.CreateWithKittenMini());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Assert.IsTrue(coordinator.IsKittenMiniAvailable);
        var settings = new CoreVoiceSettings { Volume = 0 };
        foreach (var (language, text) in new[] { ("ru-RU", "Голос ядра готов. Продолжаем работу."), ("en-US", "The core voice is ready. Let's continue.") })
        {
            var progress = new SpeechProgress();
            var result = await coordinator.PresentAsync(new([new("test", text)], language, settings, "mini_core_smoke"), progress, null, timeout.Token);
            Assert.IsTrue(result.Completed, $"{language}: {result.ErrorCode}");
            Assert.IsTrue(progress.Last?.IsComplete);
            Assert.AreEqual(text.Length, progress.Last!.VisibleCharacters["test"]);
        }
    }

    private sealed class SpeechProgress : IProgress<CoreSpeechProgress>
    {
        public CoreSpeechProgress? Last { get; private set; }
        public void Report(CoreSpeechProgress value) => Last = value;
    }

    private static CoreSpeechRequest CreateRequest(string provider) =>
        new(
            [new CoreSpeechSegment("question", "Тестовый вопрос")],
            "ru",
            new CoreVoiceSettings { Provider = provider },
            "router_test");

    private sealed class FakeVoiceEngine(bool isAvailable) : ICoreVoiceEngine
    {
        public bool IsAvailable { get; } = isAvailable;

        public int SpeakCalls { get; private set; }

        public Task<CoreSpeechPresentationResult> SpeakAsync(
            CoreSpeechRequest request,
            IProgress<CoreSpeechProgress> progress,
            CancellationToken cancellationToken)
        {
            SpeakCalls++;
            return Task.FromResult(new CoreSpeechPresentationResult(true, false, true));
        }

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }
}
