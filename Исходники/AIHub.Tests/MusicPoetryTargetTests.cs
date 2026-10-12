using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryTargetTests
{
    [TestMethod]
    public void OnlySelectedEngineAppearsInModelPromptAndNeitherSchemaOffersEngineChoices()
    {
        var localization = new LocalizationService(); localization.Load("ru");
        foreach (var selected in MusicPoetryProtocol.Variations)
        {
            var session = new MusicPoetrySession { Parameters = new(selected, "[Music]\nUpbeat Folk") };
            var prompt = MusicPoetryProtocol.System(session, MusicPoetryProtocol.Guidance(selected, localization.T));
            StringAssert.Contains(prompt, selected); StringAssert.Contains(prompt, MusicModelVariants.Name(selected));
            foreach (var other in MusicPoetryProtocol.Variations.Where(id => id != selected))
                Assert.IsFalse(prompt.Contains(other, StringComparison.Ordinal), prompt);
        }
        foreach (var format in new[] { MusicPoetryProtocol.Schema, MusicPoetryProtocol.AssemblySchema })
        {
            var schema = JsonSerializer.SerializeToElement(format).GetProperty("json_schema").GetProperty("schema");
            Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("variation", out _));
            Assert.IsFalse(schema.GetProperty("properties").TryGetProperty("actions", out _));
            CollectionAssert.AreEquivalent(new[] { "chat", "song", "parameters" }, schema.GetProperty("required")
                .EnumerateArray().Select(a => a.GetString()!).ToArray());
            foreach (var engine in MusicPoetryProtocol.Variations) Assert.IsFalse(schema.GetRawText().Contains(engine, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void EngineChangesAreRejectedBeforeAnyWorkspaceMutationEvenWithoutParser()
    {
        var session = MusicPoetryProtocol.Create(new() { Lyrics = "Original", Variation = MusicStudioRuntime.Variation }, key => key);
        foreach (var engine in MusicPoetryProtocol.Variations.Append("unknown"))
        {
            var reply = new MusicPoetryReply("Done", "Do not apply", "Do not apply", engine, ["song", "parameters"]);
            var error = Assert.ThrowsExactly<InvalidDataException>(() => MusicPoetryProtocol.Parse(JsonSerializer.Serialize(new {
                chat = reply.Chat, song = reply.Song, parameters = reply.Parameters, variation = reply.Variation, actions = reply.Actions })));
            Assert.AreEqual("Music.Poetry.TargetReadOnly", error.Message);
            Assert.AreEqual("Music.Poetry.TargetReadOnly", MusicPoetryReplyGuard.Check(session, reply).Error);
            Assert.ThrowsExactly<InvalidDataException>(() => session.Apply(reply));
            Assert.AreEqual("Original", session.Lyrics); Assert.AreEqual("", session.Parameters.Text);
            Assert.AreEqual(MusicStudioRuntime.Variation, session.Parameters.Variation);
        }
        var actionOnly = new MusicPoetryReply("", null, null, null, ["variation"]);
        Assert.AreEqual("Music.Poetry.TargetReadOnly", MusicPoetryReplyGuard.Check(session, actionOnly).Error);
        Assert.ThrowsExactly<InvalidDataException>(() => session.Apply(actionOnly));
    }

    [TestMethod]
    public async Task OldRepairAndUnsolicitedEngineHistoryAreNotReplayedButStaySaved()
    {
        var session = new MusicPoetrySession();
        var oldHuman = new MusicPoetryMessage("user", "Какой жанр посоветуешь?", "old");
        var oldRepair = new MusicPoetryMessage("user", "Change music-ace15-xl-turbo", "old") { IsProgrammatic = true };
        var oldAnswer = new MusicPoetryMessage("assistant", "Switched to another engine for demonstration", "old");
        session.Messages.AddRange([oldHuman, oldRepair, oldAnswer,
            new("assistant", "Done", "old") { IsProgrammatic = true },
            new("user", "Замени жанр на Upbeat Folk", "latest"),
            new("user", "An old repair must not get back in", "latest") { IsProgrammatic = true },
            new("user", "Keep the selected engine; only repair parameters", "latest")
                { IsProgrammatic = true, ContextRevision = MusicPoetryProtocol.ContextRevision }]);
        session.ReplyAttempts.Add(new("old", DateTimeOffset.UtcNow, 2, "{}", "applied", ["variation"]));
        var context = await MusicPoetryContextWindow.BuildAsync(session, "guide", 32768, 4096,
            (_, _) => Task.FromResult(100), true);
        Assert.AreEqual(3, context.Messages.Length); Assert.AreSame(oldHuman, context.Messages[0]);
        Assert.IsFalse(oldRepair.InContext); Assert.IsFalse(oldAnswer.InContext);
        Assert.IsTrue(session.Messages[^1].InContext); Assert.AreEqual(7, session.Messages.Count);
        Assert.IsFalse(context.Messages.Any(m => m.Text.Contains("music-ace15-xl-turbo", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GenreParametersCanChangeAndManualEngineSelectionStillUpdatesContext()
    {
        var session = new MusicPoetrySession { Lyrics = "Keep lyrics" };
        var checkedReply = MusicPoetryReplyGuard.Check(session,
            new("", null, "[Music]\nЖанр: Upbeat Folk", null, ["parameters"]));
        Assert.IsNull(checkedReply.Error); session.Apply(checkedReply.Reply);
        Assert.AreEqual("Keep lyrics", session.Lyrics); StringAssert.Contains(session.Parameters.Text, "Upbeat Folk");
        Assert.AreEqual(MusicStudioRuntime.Variation, session.Parameters.Variation);
        session.Parameters = new(MusicAceCatalog.Variation, session.Parameters.Text); session.CommitParameters();
        var prompt = MusicPoetryProtocol.System(session, "ACE requirements");
        StringAssert.Contains(prompt, MusicAceCatalog.Variation); Assert.IsFalse(prompt.Contains(MusicStudioRuntime.Variation));
    }

    [TestMethod]
    public Task LiterarySpecializationRemainsVisibleAcrossThemesExpansionAndSelection() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-hydra-card-" + Guid.NewGuid().ToString("N"));
        var screenshots = Path.Combine(Path.GetTempPath(), "lopata-hydra-card-ui");
        Directory.CreateDirectory(screenshots);
        var localization = new LocalizationService(); localization.Load("ru");
        var core = MusicPoetryModelCatalog.All.Single(c => c.Id == "core");
        var hydra = MusicPoetryModelCatalog.All.Single(c => c.Id == "profile-hydra");
        var first = new DebugModelInfo { Name = core.FileName, Path = core.FileName, Format = "gguf" };
        var second = new DebugModelInfo { Name = hydra.DisplayName!, Path = hydra.FileName, Format = "gguf" };
        try
        {
            foreach (var dark in new[] { false, true })
            {
                var preferences = new MusicPoetryPreferences(Path.Combine(root, dark + ".json"));
                var window = new MusicPoetryModelWindow([new(core, first), new(hydra, second)], second.Path,
                    localization.T, _ => { }, preferences);
                try
                {
                    window.Resources.MergedDictionaries.Add(new ResourceDictionary {
                        Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
                    Brush Color(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                    window.Resources["WindowBackgroundBrush"] = Color(dark ? "#111827" : "#F3F3F3");
                    window.Resources["PanelBrush"] = Color(dark ? "#172033" : "#FFFFFF");
                    window.Resources["InputBrush"] = window.Resources["WindowBackgroundBrush"];
                    window.Resources["TextPrimaryBrush"] = Color(dark ? "#F8FAFC" : "#1F1F1F");
                    window.Resources["TextSecondaryBrush"] = Color(dark ? "#AAB4C4" : "#5D6470");
                    window.Resources["LineBrush"] = Color(dark ? "#2D374B" : "#DADDE3");
                    var accent = Color("#2563EB"); window.Resources["AccentBrush"] = accent;
                    window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["WindowBackgroundBrush"];
                    window.Resources["UiBodyFontSize"] = 14d;
                    window.Show(); window.UpdateLayout();
                    var all = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
                    var section = all.OfType<Expander>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Poetry.ModelCard.profile-hydra");
                    var coreSection = all.OfType<Expander>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Poetry.ModelCard.core");
                    var frame = all.OfType<Border>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Poetry.SelectedCard.profile-hydra");
                    var tint = (Border)section.Template.FindName("LiteraryTint", section);
                    Assert.AreEqual(Visibility.Visible, tint.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, ((Border)coreSection.Template.FindName("LiteraryTint", coreSection)).Visibility);
                    Assert.IsTrue(all.OfType<TextBlock>().Any(t => t.Text == "Литературная основа"));
                    Assert.AreSame(accent, frame.BorderBrush);
                    foreach (var expanded in new[] { false, true })
                    {
                        section.IsExpanded = expanded; window.UpdateLayout();
                        Assert.AreEqual(Visibility.Visible, tint.Visibility);
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(850, 710, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(screenshots, $"hydra-{dark}-{expanded}.png")); encoder.Save(file);
                    }
                    all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Poetry.SelectModel.core")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreNotSame(accent, frame.BorderBrush);
                    Assert.AreEqual(Visibility.Visible, tint.Visibility);
                    Assert.IsNull(preferences.Load().FavoriteId);
                }
                finally { window.Close(); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task CollapsedCurrentCardIsHighlightedIndependentlyOfFavoriteAndTracksNewSelection() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-selection-highlight-" + Guid.NewGuid().ToString("N"));
        var preference = new MusicPoetryPreferences(Path.Combine(root, "preferences.json"));
        preference.SetFavorite("core");
        var core = MusicPoetryModelCatalog.All.Single(c => c.Id == "core");
        var kaine = MusicPoetryModelCatalog.All.Single(c => c.Id == "profile-kaine");
        var first = new DebugModelInfo { Name = core.FileName, Path = core.FileName, Format = "gguf" };
        var second = new DebugModelInfo { Name = kaine.DisplayName!, Path = kaine.FileName, Format = "gguf" };
        var window = new MusicPoetryModelWindow([new(core, first), new(kaine, second)], second.Path, key => key, _ => { }, preference);
        try
        {
            var accent = new SolidColorBrush(Colors.DodgerBlue); window.Resources["AccentBrush"] = accent;
            window.Resources["PanelBrush"] = new SolidColorBrush(Colors.Black);
            var all = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
            var frames = all.OfType<Border>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Poetry.SelectedCard."))
                .ToDictionary(b => AutomationProperties.GetAutomationId(b));
            var selectedFrame = frames["Music.Poetry.SelectedCard.profile-kaine"];
            Assert.AreSame(accent, selectedFrame.BorderBrush);
            Assert.IsFalse(all.OfType<Expander>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Poetry.ModelCard.profile-kaine").IsExpanded);
            Assert.AreEqual("Music.Poetry.Selection.Current", AutomationProperties.GetItemStatus(selectedFrame));
            all.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Poetry.SelectModel.core")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreSame(accent, frames["Music.Poetry.SelectedCard.core"].BorderBrush);
            Assert.AreEqual("", AutomationProperties.GetItemStatus(selectedFrame));
            Assert.AreEqual("core", preference.Load().FavoriteId);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
