using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicLyricsPhraseTests
{
    private static string Json(params (string Text, int From, int To)[] segments) => JsonSerializer.Serialize(new {
        transcription = segments.Select(s => new { text = s.Text, offsets = new { from = s.From, to = s.To },
            tokens = new[] { new { text = s.Text, offsets = new { from = s.From, to = s.From }, p = .9 } } })
    });
    private static MusicLyricsAlignmentResult Match(string lyrics, string json) =>
        MusicLyricsAlignmentService.MatchRecognition(json, lyrics, 100, "test/small/matcher-2");

    [TestMethod]
    public void ZeroDurationPiecePreservesTheFullWordButAnEntireZeroDurationWordHasNoTime()
    {
        const string json = """
        {"transcription":[{"text":" Слышались", "tokens":[
        {"text":" Слыша","offsets":{"from":4000,"to":4400},"p":0.9},
        {"text":"л","offsets":{"from":4400,"to":4400},"p":0.99},
        {"text":"ись","offsets":{"from":4400,"to":4900},"p":0.9}]}]}
        """;
        var words = MusicLyricsAlignmentMatcher.ReadWhisperJson(json, 100);
        Assert.HasCount(1, words); Assert.AreEqual("Слышались", words[0].Text);
        Assert.AreEqual(4, words[0].StartSeconds); Assert.AreEqual(4.9, words[0].EndSeconds);
        Assert.IsEmpty(MusicLyricsAlignmentMatcher.ReadWhisperJson(Json(("Кот", 4000, 4000)), 100));
    }
    [TestMethod]
    public void WholePhraseUsesMeasuredBoundariesWithoutInventingWordTimes()
    {
        const string lyrics = "[Verse]\nКот уже потребовал котлеты.";
        var result = Match(lyrics, Json(("Кот уже потребовал котлеты", 31000, 37000)));
        Assert.HasCount(1, result.Lines); var line = result.Lines[0];
        Assert.AreEqual(31, line.StartSeconds); Assert.AreEqual(37, line.EndSeconds); Assert.IsEmpty(line.Words);
        Assert.AreEqual("Кот уже потребовал котлеты.", lyrics.Substring(line.SourceStart, line.SourceLength));
    }
    [TestMethod]
    public void MinorRecognitionErrorAndRepeatingChorusesKeepTheirOrderedSourceOccurrences()
    {
        const string chorus = "Я и так тут двадцать песен правил.";
        var lyrics = chorus + "\nКот уже потребовал котлеты.\n" + chorus;
        var result = Match(lyrics, Json(("Я и так тут двадцать песен провел", 10000, 16000),
            ("Кот уже потребовал котлеты", 18000, 24000), ("Я и так тут двадцать песен провел", 27000, 34000)));
        Assert.HasCount(3, result.Lines); Assert.AreEqual(0, result.Lines[0].SourceStart);
        Assert.AreEqual(lyrics.LastIndexOf(chorus, StringComparison.Ordinal), result.Lines[2].SourceStart);
        Assert.AreEqual(27, result.Lines[2].StartSeconds);
    }
    [TestMethod]
    public void MissingFragmentsWrongPhrasesAndInvalidClocksRemainUnmarked()
    {
        const string lyrics = "За окном погасли фонари,\nКот уже потребовал котлеты.\nЗавтра будет песня о паштете.";
        Assert.IsEmpty(Match(lyrics, Json(("Погасли фонари", 0, 10000), ("Солнце снова вышло из-за тучи", 11000, 15000),
            ("Завтра будет песня", 20000, 24000), ("Кот уже потребовал котлеты", 40000, 39000))).Lines);
        Assert.IsEmpty(Match(lyrics, Json(("Кот уже потребовал котлеты", 99000, 110000))).Lines);
        Assert.IsEmpty(Match(lyrics, Json(("Кот уже потребовал котлеты Завтра будет песня о паштете", 31000, 42000))).Lines);
    }
}
