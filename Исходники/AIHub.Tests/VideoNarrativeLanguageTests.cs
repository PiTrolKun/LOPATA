using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoNarrativeLanguageTests
{
    [TestMethod]
    [DataRow("The camera moves slowly. He says \"Снято!\" (русская речь).")]
    [DataRow("The camera moves slowly. He says 'Снято!' [текст (по-русски)].")]
    [DataRow("The camera moves slowly. He says «Снято!». {Это примечание [с вложением]}")]
    [DataRow("The camera moves slowly. He says “Снято!” or „Стоп!“. <русский текст>")]
    [DataRow("The camera moves slowly. He says ‘Снято!’ or 「Стоп!」 or 『Тихо』.")]
    public void QuotedAndNestedBracketTextDoesNotReachDetector(string text)
    {
        var calls = 0;
        var result = VideoNarrativeLanguage.Detect(text, narrative =>
        {
            calls++; Assert.IsFalse(narrative.Any(c => c is >= 'А' and <= 'я'));
            return ["en", "fr"];
        });
        Assert.AreEqual(VideoNarrativeLanguageKind.English, result.Kind); Assert.AreEqual(1, calls);
        StringAssert.Contains(text, "Снято!");
    }

    [TestMethod]
    [DataRow("The traveller doesn't know who he's looking for. He says 'Привет'.")]
    [DataRow("The traveller’s coat is wet. He can’t find the house. He says «Привет».")]
    [DataRow("The traveller walks through the town. He says \"A \\\"word\\\" and Привет\".")]
    public void ApostrophesAndEscapedQuotesDoNotSwallowNarrative(string text)
    {
        var outside = VideoNarrativeLanguage.OutsideProtected(text, out var balanced);
        Assert.IsTrue(balanced); StringAssert.Contains(outside, "traveller"); Assert.IsFalse(outside.Contains("Привет", StringComparison.Ordinal));
        Assert.AreEqual(VideoNarrativeLanguageKind.English, VideoNarrativeLanguage.Detect(text, _ => ["en"]).Kind);
    }

    [TestMethod]
    [DataRow("The camera moves and he says \"Привет")]
    [DataRow("The camera moves [русский текст")]
    [DataRow("The camera moves (русский текст]")]
    public void UnbalancedDelimitersNeverCauseAnEnglishBypass(string text)
    {
        Assert.AreEqual(VideoNarrativeLanguageKind.Unknown, VideoNarrativeLanguage.Detect(text, _ => ["en"]).Kind);
    }

    [TestMethod]
    [DataRow("Hello")]
    [DataRow("LOPATA")]
    [DataRow("A small cat")]
    public void ShortAmbiguousInputIsNotAssumedEnglish(string text)
    {
        Assert.IsTrue(VideoNarrativeLanguage.Detect(text, _ => ["en"]).NeedsTranslation);
    }

    [TestMethod]
    [DataRow("(Снято!) [реплика] «Привет»")]
    [DataRow("123 / 45 (1 + 2)")]
    public void ProtectedOnlyInputRemainsUntouchedWithoutCallingADetector(string text)
    {
        var original = text;
        var result = VideoNarrativeLanguage.Detect(text, _ => throw new InvalidOperationException("Detector must not be called"));
        Assert.AreEqual(VideoNarrativeLanguageKind.ProtectedOnly, result.Kind); Assert.IsFalse(result.NeedsTranslation); Assert.AreEqual(original, text);
    }

    [TestMethod]
    public void LatinDoesNotMeanEnglishAndUnavailableDetectionRequiresTranslation()
    {
        Assert.IsTrue(VideoNarrativeLanguage.Detect("Un homme marche dans une ville abandonnee.", _ => ["fr", "en"]).NeedsTranslation);
        Assert.IsTrue(VideoNarrativeLanguage.Detect("The cat walks slowly. Потом он находит дом.", _ => ["en", "ru"]).NeedsTranslation);
        Assert.AreEqual(VideoNarrativeLanguageKind.Unknown, VideoNarrativeLanguage.Detect("A traveller walks through the old town.", _ => []).Kind);
    }

    [TestMethod]
    [DataRow("The traveller walks through an abandoned city. He says «Кто здесь?» (по-русски).", false)]
    [DataRow("Кот мастерит чай. Камера отлетает в сторону и разворачивается назад. Он говорит \"Снято!\"", true)]
    [DataRow("Un homme marche dans une vieille ville abandonnee et cherche une maison dans le brouillard.", true)]
    [DataRow("Ein Mann geht durch eine alte verlassene Stadt und sucht nach einem Haus im Nebel.", true)]
    [DataRow("Un hombre camina por una ciudad abandonada y busca una casa en la niebla.", true)]
    public void ActualWindowsDetectionSeparatesEnglishFromOtherLanguages(string text, bool needsTranslation)
    {
        var result = VideoNarrativeLanguage.Detect(text);
        Assert.AreNotEqual(VideoNarrativeLanguageKind.Unknown, result.Kind, "Windows ELS did not recognize the sample.");
        Assert.AreEqual(needsTranslation, result.NeedsTranslation);
    }

    [TestMethod]
    public void LegacyManualFlagDoesNotOverrideDetectionAndAutomaticCopiesPreserveManualWork()
    {
        var p = JsonSerializer.Deserialize<VideoPreparationProject>("{\"Form\":\"story\",\"IdeaText\":\"Кот мастерит чай. Камера смотрит на него.\",\"IdeaAlreadyEnglish\":true}")!;
        Assert.IsTrue(VideoNarrativeLanguage.Detect(p.IdeaText).NeedsTranslation); p.Validate();
        p.IdeaText = "The cat walks across a quiet old town."; VideoTranslationHistory.UseOriginal(p);
        p.EnglishText = "Preserve my manual wording."; VideoTranslationHistory.Capture(p);
        p.IdeaText = "The cat walks across an abandoned old city."; VideoConceptHistory.Capture(p);
        VideoTranslationHistory.UseOriginal(p, select: false); VideoTranslationHistory.UseOriginal(p, select: false);
        Assert.AreEqual("Preserve my manual wording.", p.EnglishText); Assert.HasCount(3, p.TranslationHistory);
        Assert.IsTrue(VideoTranslationHistory.Move(p, 1)); Assert.AreEqual(p.IdeaText, p.EnglishText);
        p.Validate();
    }
}
