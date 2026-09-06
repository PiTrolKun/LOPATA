using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PromptPairTests
{
    private string _directory = null!;
    private string Catalog => Path.Combine(_directory, "pairs.json");

    [TestInitialize]
    public void Initialize() { _directory = Path.Combine(Path.GetTempPath(), "LOPATA_PromptTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory); }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static PromptPairPreset Pair(string name = "My prompt") => new()
    {
        ContractId = OmniPromptPairAdapter.ContractId, Name = name,
        AnalysisPrompt = "Inspect signs and objects.", ComposePrompt = "Explain the signs."
    };

    [TestMethod]
    public void CustomPrompts_IgnoreEveryStandardSettingAndKeepWishesAndContract()
    {
        var settings = new ImageAnalysisLiterarySettings
        { PromptMode = PromptModes.Custom, CustomPrompts = Pair(), LanguageCode = "en", Wishes = "Read small signs" };
        var observation = ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(settings);
        var compose = ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings);
        settings.Accuracy = "ignored-accuracy"; settings.Style = "ignored-style";
        settings.Length = "ignored-length"; settings.Form = "ignored-form";
        Assert.AreEqual(observation, ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(settings));
        Assert.AreEqual(compose, ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings));
        StringAssert.Contains(observation, "Inspect signs and objects.");
        StringAssert.Contains(observation, settings.Wishes);
        StringAssert.Contains(compose, "Explain the signs.");
        StringAssert.Contains(compose, settings.Wishes);
        Assert.AreEqual(1, compose.Split("Return exactly one JSON object").Length - 1);
        foreach (var field in new[] { "title:", "paragraphs:", "review_items:", "uncertainties:" }) StringAssert.Contains(compose, field);
        settings.LanguageCode = "ru";
        StringAssert.Contains(ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings), "Все текстовые значения — на русском");
    }

    [TestMethod]
    public void StandardMode_IgnoresRetainedCustomSnapshot()
    {
        var settings = new ImageAnalysisLiterarySettings { Wishes = "проверить текст" };
        var before = ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings);
        settings.CustomPrompts = Pair();
        Assert.AreEqual(before, ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings));
    }

    [TestMethod]
    public void DefaultEditorTexts_HaveNoServiceSchemaAndAreIndependentCopies()
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var pair = OmniPromptPairAdapter.CreateDefault(language);
            Assert.IsFalse(pair.ComposePrompt.Contains("review_items"));
            Assert.IsFalse(pair.ComposePrompt.Contains("{{"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(pair.AnalysisPrompt));
            Assert.AreNotEqual(pair.Id, OmniPromptPairAdapter.CreateDefault(language).Id);
        }
    }

    [TestMethod]
    public void MissingOrIncompatibleCustomPreset_IsRejectedWithoutStandardFallback()
    {
        var settings = new ImageAnalysisLiterarySettings { PromptMode = PromptModes.Custom };
        Assert.ThrowsExactly<InvalidDataException>(() => ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(settings));
        settings.CustomPrompts = Pair() with { ContractId = "other-scenario/v1" };
        Assert.ThrowsExactly<InvalidDataException>(() => ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings));
    }

    [TestMethod]
    public void Store_PreservesOrderNamesAndBothTextsAcrossRestart()
    {
        var store = new PromptPairStore(Catalog); Assert.HasCount(0, store.Load());
        var one = Pair("Первый"); var two = Pair("Second");
        store.Save([one, two]);
        store.Save([two with { Name = "Renamed" }, one]);
        var restored = new PromptPairStore(Catalog).Load();
        Assert.AreEqual(two.Id, restored[0].Id); Assert.AreEqual("Renamed", restored[0].Name);
        Assert.AreEqual(one, restored[1]);
        store.Save([one]); Assert.HasCount(1, new PromptPairStore(Catalog).Load());
    }

    [TestMethod]
    public void Store_RejectsDuplicateAndBlankWithoutChangingFile()
    {
        var store = new PromptPairStore(Catalog); store.Load(); var pair = Pair("First"); store.Save([pair]);
        var original = File.ReadAllText(Catalog);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save([pair, Pair(" first ")]));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save([pair with { ComposePrompt = " " }]));
        Assert.AreEqual(original, File.ReadAllText(Catalog));
    }

    [TestMethod]
    public void Store_DetectsConcurrentChangeAndCorruptionWithoutOverwriting()
    {
        var first = new PromptPairStore(Catalog); var second = new PromptPairStore(Catalog);
        first.Load(); second.Load(); first.Save([Pair("First")]);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.Save([Pair("Second")]));
        File.WriteAllText(Catalog, "broken");
        Assert.ThrowsExactly<JsonException>(() => new PromptPairStore(Catalog).Load());
        Assert.ThrowsExactly<InvalidOperationException>(() => first.Save([]));
        Assert.AreEqual("broken", File.ReadAllText(Catalog));
        File.WriteAllText(Catalog, "{}");
        Assert.ThrowsExactly<JsonException>(() => new PromptPairStore(Catalog).Load());
        Assert.AreEqual("{}", File.ReadAllText(Catalog));
    }

    [TestMethod]
    public void SessionSnapshot_SurvivesPresetDeletionAndKeepsRevisionConversation()
    {
        var store = new PromptPairStore(Catalog); store.Load(); var pair = Pair(); store.Save([pair]);
        var session = new ImageAnalysisLiterarySession
        {
            Settings = new() { PromptMode = PromptModes.Custom, CustomPrompts = pair, Wishes = "focus", LanguageCode = "en" }
        };
        var storage = new StorageSettings();
        storage.Results.Locations.Add(new() { Path = _directory });
        var sessions = new ImageAnalysisSessionStore(); sessions.Save(session, storage);
        store.Save([pair with { ComposePrompt = "Changed" }]); store.Save([]);
        var restored = sessions.Load(session.SessionId, storage)!;
        Assert.AreEqual(pair, restored.Settings.CustomPrompts);
        Assert.AreEqual("focus", restored.Settings.Wishes);
        Assert.AreEqual("en", restored.Settings.LanguageCode);
        StringAssert.Contains(ImageAnalysisOmniPromptBuilder.BuildComposePrompt(restored.Settings), "Explain the signs.");
        Assert.IsFalse(ImageAnalysisOmniPromptBuilder.BuildRevisionPrompt(restored.Settings, "shorter").Contains("Changed"));
    }

    [TestMethod]
    public void OldSessionWithoutPromptFields_DefaultsToStandard()
    {
        var old = JsonSerializer.Deserialize<ImageAnalysisLiterarySession>("""{"Settings":{"Wishes":"old wish"},"SchemaVersion":3}""")!;
        Assert.AreEqual(PromptModes.Standard, old.Settings.PromptMode);
        Assert.IsNull(old.Settings.CustomPrompts);
        StringAssert.Contains(ImageAnalysisOmniPromptBuilder.BuildComposePrompt(old.Settings), "old wish");
    }
}
