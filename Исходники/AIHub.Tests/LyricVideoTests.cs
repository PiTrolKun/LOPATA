using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.LyricVideo;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricVideoTests
{
    [TestMethod]
    public void RainSourcesCanBeMixedOrIndependentlyDisabled()
    {
        var options = new ClipOptions { ExtraRainText = "Тёплый ветер\nВольная музыка" };
        CollectionAssert.Contains(options.RainWords("Зелёный дождь"), "Тёплый ветер");
        CollectionAssert.Contains(options.RainWords("Зелёный дождь"), "Зелёный");
        Assert.IsFalse((options with { SongWords = false }).RainWords("Зелёный дождь").Contains("Зелёный"));
        Assert.HasCount(0, (options with { ExtraRainText = "", SongWords = false }).RainWords("Зелёный дождь"));
        var longSong = string.Join('\n', Enumerable.Range(0, 2000).Select(i => "Слово" + i));
        CollectionAssert.Contains(options.RainWords(longSong), "Вольная");
        Assert.IsLessThanOrEqualTo(1024, options.RainWords(longSong).Length);
    }
    [TestMethod]
    public void PreferencesRoundTripAndRejectUnsafeRanges()
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var preferences = new ClipPreferences(Path.Combine(root, "prefs.json"));
            var options = new ClipOptions { Height = 720, Effects = 2, TextSize = 2, ExtraRainText = "ЛОПАТА ✨", Palette = "amber", SongWords = false };
            preferences.Save(options); Assert.AreEqual(options, preferences.Load());
            foreach (var invalid in new[] { options with { Height = 1 }, options with { Palette = "bad" }, options with { Effects = -1 },
                options with { TextSize = 1000 }, options with { ExtraRainText = new string('я', 4001) } })
                Assert.Throws<InvalidDataException>(invalid.Validate);
            preferences.Save(new()); Assert.AreEqual(new ClipOptions(), preferences.Load());
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void WeatherNeverTurnsSilenceIntoStormAndSeekingIsDeterministic()
    {
        var analysis = new AudioAnalysis { Duration = 2, Rms95 = 1, Points = Enumerable.Range(0, 100)
            .Select(i => new AnalysisPoint(i / 50d, 0, 0, 0, 0, i < 50 ? default : new(1, 1, 1, 1, 0))).ToArray() };
        foreach (var scale in new[] { .7f, 1, 1.22f })
        {
            var weather = new MusicWeather(analysis, scale);
            Assert.AreEqual(0, weather.At(.8).Storm);
            Assert.IsGreaterThan(.5f, weather.At(1.9).Storm);
            var value = weather.At(1.7); weather.At(.2); Assert.AreEqual(value, weather.At(1.7));
            Assert.IsLessThan(weather.At(1.5).RainTravel, weather.At(1.4).RainTravel);
        }
    }
    [TestMethod]
    public void RenderPreservesClockAndCoverAcrossAllOptions()
    {
        var session = Synthetic(); var expected = session.Lyrics!.FindLine(30);
        foreach (var palette in new[] { "green", "cyan", "amber" })
        foreach (var textSize in new[] { 0, 1, 2 })
        {
            using var renderer = new SceneRenderer(session, new() { Palette = palette, TextSize = textSize, ExtraRainText = "Тестовый ветер" });
            var frame = SHA256.HashData(renderer.Render(30)); renderer.Render(20);
            CollectionAssert.AreEqual(frame, SHA256.HashData(renderer.Render(30)));
            Assert.AreSame(expected, session.Lyrics.FindLine(30));
            Assert.IsGreaterThan(0, renderer.LyricRainWordCount);
        }
    }
    [TestMethod]
    public void InheritedMusicTagsKeepOriginalHistoryAndSeparateVideoOrigin()
    {
        var session = Synthetic(); session.SourceTags["comment"] = "Комментарий автора\nВторая строка";
        session.SourceTags["LOPATA_VERSION"] = "0.5.12-dev"; session.SourceTags["encoder"] = "original music encoder";
        session.SourceTags["LOPATA_TIMED_LYRICS"] = "original-audio-clock";
        var tags = ClipMetadata.Create(session, "run", new());
        Assert.AreEqual(session.SourceTags["comment"], tags["comment"]);
        Assert.AreEqual("0.5.12-dev", tags["LOPATA_VERSION"]);
        Assert.AreEqual("original music encoder", tags["LOPATA_SOURCE_encoder"]);
        Assert.AreEqual("original-audio-clock", tags["LOPATA_SOURCE_LOPATA_TIMED_LYRICS"]);
        Assert.IsTrue(JsonDocument.Parse(tags["LOPATA_VIDEO"]).RootElement.GetProperty("Program").GetString() == "LOPATA");
    }
    [TestMethod]
    public void MissingTimingStillRendersVisualizerWithoutInventingLines()
    {
        var original = Synthetic();
        var session = new ClipSession { Config = original.Config, SourceHash = original.SourceHash, PcmPath = original.PcmPath,
            WavePath = original.WavePath, Artist = original.Artist, Title = original.Title, SourceLyrics = original.SourceLyrics,
            SourceTags = original.SourceTags, Analysis = original.Analysis, Provenance = original.Provenance, Localize = original.Localize };
        using var renderer = new SceneRenderer(session, new() { SongWords = false, ExtraRainText = "Свой дождь" });
        Assert.IsNull(session.Lyrics);
        Assert.IsGreaterThan(0, renderer.LyricRainWordCount);
        Assert.HasCount(1920 * 1080 * 4, renderer.Render(30));
    }
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task SettingsAreLocalizedResettableAndFitMinimumWindow(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load(language);
        var settings = new LyricVideoSettings(l.T); var host = new Window { Width = 300, Height = 800, Content = new ScrollViewer { Content = settings } };
        try
        {
            host.Show(); host.UpdateLayout(); settings.Set(new() { SongWords = false, ExtraRainText = "Космос", Height = 720, Palette = "cyan" });
            Assert.AreEqual("Космос", settings.Options.ExtraRainText);
            settings.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(new ClipOptions(), settings.Options);
            foreach (var label in settings.Children.OfType<TextBlock>()) Assert.IsFalse(label.Text.StartsWith("LyricVideo."));
            Assert.IsTrue(settings.Children.OfType<ComboBox>().All(c => c.ActualWidth > 80));
            Assert.AreEqual("utilities_video", ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.LyricVideo).ParentId);
            Assert.AreEqual(ScenarioNavigationCatalog.LyricVideo, ScenarioNavigationCatalog.GetTag("matrix_rain").TargetId);
        }
        finally { host.Close(); }
    });
    internal static ClipSession Synthetic() => new()
    {
        Config = new(Path.Combine(Path.GetTempPath(), "source.mp3"), Path.GetTempPath()), SourceHash = new('A', 64),
        WavePath = "unused", PcmPath = "unused", Artist = "ЛОПАТА", Title = "Проверка", SourceLyrics = "Небо и звёзды",
        SourceTags = new(StringComparer.OrdinalIgnoreCase), Provenance = new() { Scenario = "music-video/test" },
        Analysis = new() { Duration = 60, Points = Enumerable.Range(0, 3000).Select(i => new AnalysisPoint(i / 50d, .5f, .5f, .5f, .5f, new(.5f, .5f, .5f, .5f, 0))).ToArray(), Rms95 = .5f },
        Localize = key => key,
        Lyrics = new() { JobId = Guid.NewGuid().ToString("N"), Variant = 0, SourceLyrics = "Небо и звёзды",
            LyricsHash = AIHub.Models.MusicTimedLyrics.HashLyrics("Небо и звёзды"), AudioPayloadHash = new('A', 64),
            Provider = "test", ProviderVersion = "test", DurationSeconds = 60, Lines = [new(0, 13, 29, 32)] }
    };
}
