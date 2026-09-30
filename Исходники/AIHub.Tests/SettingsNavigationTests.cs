using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class SettingsNavigationTests
{
    private static readonly SettingsSearchEntry[] Entries =
    [
        new("volume", "voice", "volume", "voice-help", "voice-words"),
        new("updates", "updates", "updates", "update-help", "update-words")
    ];
    private static readonly Dictionary<string, string> Ru = new()
    {
        ["volume"] = "Громкость голоса", ["voice-help"] = "Местный голос ядра", ["voice-words"] = "речь озвучивание",
        ["updates"] = "Обновления", ["update-help"] = "Скачивание патчей", ["update-words"] = "бета"
    };
    private static readonly Dictionary<string, string> En = new()
    {
        ["volume"] = "Voice volume", ["voice-help"] = "Local core voice", ["voice-words"] = "speech narration",
        ["updates"] = "Updates", ["update-help"] = "Download patches", ["update-words"] = "beta"
    };

    [TestMethod]
    public void Search_CombinesTokensAcrossLocalizedLabelsAndSynonyms()
    {
        Assert.AreEqual("volume", SettingsSearchIndex.Find(Entries, "ГРОМКОСТЬ — речь", key => Ru[key]).Single().Id);
        Assert.AreEqual("updates", SettingsSearchIndex.Find(Entries, "BETA patches", key => En[key]).Single().Id);
        Assert.AreEqual(0, SettingsSearchIndex.Find(Entries, "beta voice", key => En[key]).Count);
        Assert.AreEqual(0, SettingsSearchIndex.Find(Entries, "   ", key => En[key]).Count);
    }

    [TestMethod]
    public void Search_NormalizesYoAndUnicode()
    {
        var entry = new SettingsSearchEntry("x", "general", "x", "x", "x");
        Assert.AreEqual(1, SettingsSearchIndex.Find([entry], "свёртывание", _ => "СвЕРтывание").Count);
        Assert.AreEqual(1, SettingsSearchIndex.Find([entry], "ＰＡＴＣＨ", _ => "patch").Count);
    }

    [TestMethod]
    public void OldSettings_PreserveExistingValuesAndDefaultToCloseQuestion()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"languageCode":"en","detailedLiteraryDiagnostics":false,"interface":{"textScalePercent":125},
             "modelDownloads":{"maximumParallelConnections":4},"coreVoice":{"enabled":true}}
            """, options)!;
        Assert.IsFalse(settings.Behavior.CloseToTray);
        Assert.IsTrue(settings.Behavior.AskBeforeClosing);
        Assert.IsFalse(settings.Behavior.LaunchWithWindows);
        Assert.AreEqual("general", settings.Behavior.LastSettingsSection);
        Assert.AreEqual("en", settings.LanguageCode);
        Assert.AreEqual(125, settings.Interface.TextScalePercent);
        Assert.AreEqual(4, settings.ModelDownloads.MaximumParallelConnections);
        settings.Behavior.CloseToTray = true; settings.Behavior.AskBeforeClosing = false;
        settings.Behavior.LastSettingsSection = "components";
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        Assert.IsTrue(restored.Behavior.CloseToTray);
        Assert.IsFalse(restored.Behavior.AskBeforeClosing);
        Assert.AreEqual("components", restored.Behavior.LastSettingsSection);
        Assert.IsFalse(restored.DetailedLiteraryDiagnostics);
    }

    [TestMethod]
    public void CloseQuestion_DoesNotInterruptExplicitExitOrShutdown()
    {
        Assert.IsTrue(ApplicationClosePolicy.ShouldAsk(true, false, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldAsk(false, false, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldAsk(true, true, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldAsk(true, false, true, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldAsk(true, false, false, true, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldAsk(true, false, false, false, true));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var prior = JsonSerializer.Deserialize<AppSettings>("""{"behavior":{"closeToTray":true}}""", options)!;
        Assert.IsTrue(prior.Behavior.CloseToTray);
        Assert.IsTrue(prior.Behavior.AskBeforeClosing);
    }

    [TestMethod]
    public void Tray_OnlyInterceptsOrdinaryCloseWithARecoveryIcon()
    {
        Assert.IsTrue(ApplicationClosePolicy.ShouldHide(true, true, false, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(false, true, false, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(true, false, false, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(true, true, true, false, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(true, true, false, true, false, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(true, true, false, false, true, false));
        Assert.IsFalse(ApplicationClosePolicy.ShouldHide(true, true, false, false, false, true));
    }
}
