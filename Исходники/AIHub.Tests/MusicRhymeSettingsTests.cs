using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeSettingsTests
{
    [TestMethod]
    public void LegacyPreferencesWithoutTuningKeepExistingChoicesAndComputationalDefaults()
    {
        WithStore((path, preferences) => {
            File.WriteAllText(path, """{"Color":"#528EE8","SelectionOnly":true,"AutoAnalyze":false,"ModelId":"core"}""");
            var loaded = preferences.Load();
            Assert.AreEqual("#528EE8", loaded.Color);
            Assert.IsTrue(loaded.SelectionOnly);
            Assert.IsFalse(loaded.AutoAnalyze);
            Assert.AreEqual("core", loaded.ModelId);
            Assert.IsFalse(loaded.ExpertMode);
            Assert.AreEqual(0, loaded.Tuning.Values.Count);
            Assert.AreEqual(3, loaded.Tuning.Integer("analysis.max_fragments"));
            Assert.AreEqual(.2, loaded.Tuning.Number("analysis.line_share"));
            Assert.AreEqual(8192, loaded.Tuning.Integer("ReplyTokens"));
        });
    }

    [TestMethod]
    public void InvalidNumericUnknownAndNullValuesCannotEnterSavedPreferences()
    {
        WithStore((path, preferences) => {
            preferences.Save(new()); var original = File.ReadAllBytes(path);
            foreach (var invalid in new[] {
                Tuning("analysis.max_fragments", -1), Tuning("analysis.max_fragments", 1.5),
                Tuning("analysis.line_share", double.NaN), Tuning("analysis.line_share", double.PositiveInfinity),
                Tuning("analysis.line_share", 1.1), Tuning("unknown.setting", 1),
                Tuning("Reasoning", .5), new MusicRhymeTuning { Values = null! }
            })
            {
                Assert.Throws<InvalidDataException>(invalid.Validate);
                Assert.Throws<InvalidDataException>(() => preferences.Save(new() { Tuning = invalid }));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            }
        });
    }

    [TestMethod]
    public void IndependentRequestAndCheckpointSnapshotsCompareByEffectiveValues()
    {
        var request = new MusicRhymeRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "Кот", 0, 3,
            "core", null, "", "ru") { Tuning = Tuning("analysis.max_fragments", 7) };
        var input = JsonSerializer.Deserialize<MusicRhymeRequest>(JsonSerializer.Serialize(request))!;
        var checkpoint = JsonSerializer.Deserialize<MusicRhymeStep>(JsonSerializer.Serialize(new MusicRhymeStep { Request = request }))!;
        Assert.AreEqual(input, checkpoint.Request);
        Assert.AreEqual(input.GetHashCode(), checkpoint.Request.GetHashCode());
        checkpoint.Request.Tuning.Values["analysis.max_fragments"] = 8;
        Assert.AreNotEqual(input, checkpoint.Request);
        Assert.AreEqual(new MusicRhymeTuning(), Tuning("analysis.max_fragments", 3));
    }

    [TestMethod]
    public void CatalogHasUniqueIdsValidDefaultsAndOnlyTheNineVisibleGroups()
    {
        var entries = MusicRhymeSettingCatalog.All;
        Assert.AreEqual(entries.Count, entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.AreEquivalent(MusicRhymeSettingsPanel.GroupIds,
            entries.Select(item => item.Group).Distinct(StringComparer.Ordinal).ToArray());
        foreach (var setting in entries)
        {
            Assert.IsTrue(setting.Accepts(setting.Default), setting.Id);
            Assert.IsFalse(setting.Accepts(double.NaN), setting.Id);
            Assert.IsFalse(setting.Accepts(double.PositiveInfinity), setting.Id);
        }
    }

    [TestMethod]
    public void SnapshotsAndIsolatedPreferenceRoundTripDoNotShareMutableValues()
    {
        WithStore((_, preferences) => {
            var options = new MusicRhymeOptions { Tuning = Tuning("analysis.max_fragments", 7), ExpertMode = true };
            var snapshot = options.Snapshot(); preferences.Save(snapshot);
            options.Tuning.Values["analysis.max_fragments"] = 11;
            Assert.AreEqual(7, snapshot.Tuning.Integer("analysis.max_fragments"));
            var loaded = preferences.Load(); Assert.AreEqual(7, loaded.Tuning.Integer("analysis.max_fragments"));
            Assert.IsTrue(loaded.ExpertMode);
            loaded.Tuning.Values["analysis.max_fragments"] = 2;
            Assert.AreEqual(7, preferences.Load().Tuning.Integer("analysis.max_fragments"));
        });
    }

    [TestMethod]
    public Task OpeningSettingsStartsAllNineGroupsCollapsedInBothLanguages() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl();
        var localization = new LocalizationService();
        foreach (var language in new[] { "ru", "en" })
        {
            localization.Load(language);
            var panel = new MusicRhymeSettingsPanel(view.Rhyme, localization.T, () => { });
            Expert(panel, true);
            var groups = Desc<Expander>(panel).ToArray(); Assert.AreEqual(9, groups.Length);
            CollectionAssert.AreEquivalent(MusicRhymeSettingsPanel.GroupIds.Select(id => "Music.Rhyme.Settings.Group." + id).ToArray(),
                groups.Select(AutomationProperties.GetAutomationId).ToArray());
            Assert.IsTrue(groups.All(group => !group.IsExpanded));
            foreach (var setting in MusicRhymeSettingCatalog.All)
            {
                Assert.AreNotEqual(setting.LabelKey, localization.T(setting.LabelKey), setting.Id);
                Assert.AreNotEqual(setting.HintKey, localization.T(setting.HintKey), setting.Id);
            }
            Assert.IsTrue(Desc<Button>(panel).Any(button => Id(button) == "Music.Rhyme.Settings.Apply"));
        }
    });

    [TestMethod]
    public Task NumericEditingDefaultsAndCancelAreDraftOnlyUntilApply() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl();
        var before = JsonSerializer.Serialize(view.Rhyme.Options);
        var closed = 0;
        var panel = new MusicRhymeSettingsPanel(view.Rhyme, key => key, () => closed++);
        Expert(panel, true);
        Input(panel, "analysis.max_fragments").Text = "12";
        Desc<System.Windows.Controls.CheckBox>(panel).Single(check => Id(check) == "Music.Rhyme.Settings.SelectionOnly").IsChecked = true;
        Click(panel, "Music.Rhyme.Color.E85959");
        Assert.AreEqual(before, JsonSerializer.Serialize(view.Rhyme.Options));
        Click(panel, "Music.Rhyme.Settings.Defaults");
        Assert.IsFalse(panel.CaptureOptions().ExpertMode);
        Assert.AreEqual(3d, Range(panel, "analysis.max_fragments").Value);
        Assert.AreEqual(80, panel.CaptureOptions().Tuning.Integer("search.lexicon_records"));
        Assert.IsFalse(Desc<System.Windows.Controls.CheckBox>(panel).Single(check => Id(check) == "Music.Rhyme.Settings.SelectionOnly").IsChecked);
        Assert.IsTrue(Desc<Expander>(panel).All(group => !group.IsExpanded));
        Assert.AreEqual(before, JsonSerializer.Serialize(view.Rhyme.Options));
        Expert(panel, true); Input(panel, "analysis.max_fragments").Text = "not a number";
        Click(panel, "Music.Rhyme.Settings.Cancel");
        Assert.AreEqual(1, closed);
        Assert.AreEqual(before, JsonSerializer.Serialize(view.Rhyme.Options));
    });

    [TestMethod]
    public Task InvalidApplyDoesNotSaveOrCloseTheDraft() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl();
        var before = JsonSerializer.Serialize(view.Rhyme.Options); var closed = 0;
        var localizer = new LocalizationService(); localizer.Load("ru");
        var panel = new MusicRhymeSettingsPanel(view.Rhyme, localizer.T, () => closed++);
        Expert(panel, true);
        Input(panel, "analysis.max_fragments").Text = "-1";
        Click(panel, "Music.Rhyme.Settings.Apply");
        Assert.AreEqual(0, closed);
        Assert.AreEqual(before, JsonSerializer.Serialize(view.Rhyme.Options));
        Assert.IsTrue(Desc<TextBlock>(panel).Any(block => block.Text.Contains(localizer.T("Music.Rhyme.Settings.analysis.max_fragments"), StringComparison.Ordinal)
            && block.Text != localizer.T("Music.Rhyme.Settings.analysis.max_fragments")));
    });

    [TestMethod]
    public Task ValidApplyPersistsTheControlsToAnIsolatedStoreAndKeepsTheSong() => ScenarioNavigationTests.Sta(() =>
    {
        WithStore((_, preferences) => {
            using var view = new MusicWorkspaceControl(); view.Editor.Lyrics = "Кот поёт у окна.";
            using var owner = new MusicRhymeController(view, preferences);
            var closed = 0; var localizer = new LocalizationService(); localizer.Load("ru");
            var panel = new MusicRhymeSettingsPanel(owner, localizer.T, () => closed++);
            Expert(panel, true);
            Input(panel, "analysis.max_fragments").Text = "7";
            Input(panel, "analysis.line_share").Text = "0,75";
            Desc<System.Windows.Controls.CheckBox>(panel).Single(check => Id(check) == "Music.Rhyme.Settings.AutoAnalyze").IsChecked = false;
            Click(panel, "Music.Rhyme.Color.528EE8"); Click(panel, "Music.Rhyme.Settings.Apply");
            Assert.AreEqual(1, closed);
            var saved = preferences.Load(); Assert.AreEqual(7, saved.Tuning.Integer("analysis.max_fragments"));
            Assert.AreEqual(.75, saved.Tuning.Number("analysis.line_share"));
            Assert.AreEqual("#528EE8", saved.Color); Assert.IsFalse(saved.AutoAnalyze);
            Assert.AreEqual(saved.Tuning, owner.Options.Tuning);
            Assert.AreEqual("Кот поёт у окна.", view.Editor.Lyrics);
        });
    });

    [TestMethod]
    public Task NewPreferencesOpenInSimpleModeWithOnlyFourSlidersAndBasicControls() => ScenarioNavigationTests.Sta(() =>
    {
        WithStore((_, preferences) => {
            using var view = new MusicWorkspaceControl(); using var owner = new MusicRhymeController(view, preferences);
            var panel = new MusicRhymeSettingsPanel(owner, key => key, () => { });
            Assert.IsFalse(panel.CaptureOptions().ExpertMode);
            Assert.AreEqual(0, Desc<Expander>(panel).Count());
            CollectionAssert.AreEquivalent(MusicRhymeSettingsPanel.SimpleSettingIds.Select(id => "Music.Rhyme.Setting." + id).ToArray(),
                Desc<Slider>(panel).Select(Id).ToArray());
            Assert.AreEqual(0, Desc<TextBox>(panel).Count());
            Assert.IsTrue(Desc<ComboBox>(panel).Any(item => Id(item) == "Music.Rhyme.Settings.Model"));
            Assert.AreEqual(7, Desc<Button>(panel).Count(item => Id(item).StartsWith("Music.Rhyme.Color.", StringComparison.Ordinal)));
            Range(panel, "analysis.line_share").Value = 75;
            Assert.AreEqual(.75, panel.CaptureOptions().Tuning.Number("analysis.line_share"));
        });
    });

    [TestMethod]
    public Task ModeSwitchKeepsTheDraftAndSimpleApplyPreservesHiddenExpertValues() => ScenarioNavigationTests.Sta(() =>
    {
        WithStore((_, preferences) => {
            preferences.Save(new() { Tuning = Tuning("search.lexicon_records", 143) });
            using var view = new MusicWorkspaceControl(); using var owner = new MusicRhymeController(view, preferences);
            var closed = 0; var panel = new MusicRhymeSettingsPanel(owner, key => key, () => closed++);
            Range(panel, "analysis.max_fragments").Value = 5;
            Expert(panel, true);
            Assert.AreEqual("5", Input(panel, "analysis.max_fragments").Text);
            Assert.AreEqual("143", Input(panel, "search.lexicon_records").Text);
            Assert.AreEqual(9, Desc<Expander>(panel).Count());
            Assert.IsTrue(Desc<Expander>(panel).All(item => !item.IsExpanded));
            Input(panel, "search.lexicon_records").Text = "144";
            Input(panel, "analysis.line_share").Text = "0,75";
            Expert(panel, false);
            Assert.AreEqual(75d, Range(panel, "analysis.line_share").Value);
            Assert.AreEqual(143, preferences.Load().Tuning.Integer("search.lexicon_records"));
            Assert.AreEqual(144, panel.CaptureOptions().Tuning.Integer("search.lexicon_records"));
            Click(panel, "Music.Rhyme.Settings.Apply");
            Assert.AreEqual(1, closed);
            var saved = preferences.Load(); Assert.IsFalse(saved.ExpertMode);
            Assert.AreEqual(144, saved.Tuning.Integer("search.lexicon_records"));
            Assert.AreEqual(5, saved.Tuning.Integer("analysis.max_fragments"));
            Assert.AreEqual(.75, saved.Tuning.Number("analysis.line_share"));
        });
    });

    [TestMethod]
    public Task InvalidExpertValueBlocksModeSwitchWithoutLosingTheDraft() => ScenarioNavigationTests.Sta(() =>
    {
        WithStore((_, preferences) => {
            using var view = new MusicWorkspaceControl(); using var owner = new MusicRhymeController(view, preferences);
            var panel = new MusicRhymeSettingsPanel(owner, key => key, () => { });
            Expert(panel, true); Input(panel, "search.lexicon_records").Text = "not a number";
            Expert(panel, false);
            Assert.IsTrue(Desc<System.Windows.Controls.CheckBox>(panel).Single(item => Id(item) == "Music.Rhyme.Settings.ExpertMode").IsChecked);
            Assert.AreEqual("not a number", Input(panel, "search.lexicon_records").Text);
            Assert.IsFalse(owner.Options.ExpertMode);
        });
    });

    [TestMethod]
    public Task SettingsRemainUsableInNarrowWindowsAndRenderBothLanguages() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl();
        foreach (var (language, width, dark) in new[] { ("ru", 1100d, true), ("en", 700d, false) })
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var resources = ScenarioNavigationTests.Create(language, 1.25, dark).Resources;
            resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke;
            resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
            var window = new MusicRhymeWindow(view.Rhyme, view.Projects.Current.Id, localizer.T, resources)
                { Width = width, Height = 700 };
            try
            {
                window.Show(); window.ShowSettings(); window.UpdateLayout();
                var panel = Desc<MusicRhymeSettingsPanel>(window).Single();
                Expert(panel, false); window.UpdateLayout();
                Assert.AreEqual(4, Desc<Slider>(panel).Count()); Capture(language + "-simple");
                Expert(panel, true); window.UpdateLayout();
                Assert.IsTrue(Desc<Expander>(panel).All(item => !item.IsExpanded));
                Capture(language + "-collapsed");
                var groupId = language == "ru" ? "analysis" : "model";
                var expanded = Desc<Expander>(panel).Single(item => Id(item) == "Music.Rhyme.Settings.Group." + groupId);
                expanded.IsExpanded = true;
                window.UpdateLayout();
                var scroll = Desc<ScrollViewer>(panel).Single();
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + expanded.TranslatePoint(new(0, 0), scroll).Y);
                window.UpdateLayout();
                foreach (var action in new[] { "Apply", "Cancel", "Defaults" })
                {
                    var button = Desc<Button>(panel).Single(item => Id(item) == "Music.Rhyme.Settings." + action);
                    Assert.IsTrue(button.ActualWidth > 0);
                    var point = button.TranslatePoint(new(0, 0), panel);
                    Assert.IsTrue(point.X >= 0 && point.X + button.ActualWidth <= panel.ActualWidth + 1);
                    Assert.IsTrue(point.Y >= 0 && point.Y + button.ActualHeight <= panel.ActualHeight + 1);
                }
                Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
                Capture(language + "-expanded");
            }
            finally { window.Close(); }
            void Capture(string name)
            {
                if (Environment.GetEnvironmentVariable("AIHUB_RHYME_SETTINGS_UI") is not { Length: > 0 } directory) return;
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
            }
        }
    });

    [TestMethod]
    public Task CompactSelectorAndSettingsRemainAvailableWithoutHistory() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var window = new MusicRhymeWindow(view.Rhyme, view.Projects.Current.Id, localizer.T);
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new(1200, 680)); content.Arrange(new(0, 0, 1200, 680)); content.UpdateLayout();
            var selector = Desc<ComboBox>(window).Single(item => Id(item) == "Music.Rhyme.Steps");
            var gear = Desc<Button>(window).Single(item => Id(item) == "Music.Rhyme.Settings.Open");
            Assert.IsFalse(selector.IsEnabled); Assert.IsTrue(gear.IsEnabled);
            Assert.IsTrue(selector.ActualWidth > 140 && selector.ActualWidth < content.ActualWidth * .45);
            Assert.IsTrue(gear.ActualWidth > 0);
            gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var panel = Desc<MusicRhymeSettingsPanel>(window).Single(); Expert(panel, false);
            Assert.AreEqual(0, Desc<Expander>(window).Count(item => Id(item).StartsWith("Music.Rhyme.Settings.Group.", StringComparison.Ordinal)));
            Assert.AreEqual(4, Desc<Slider>(panel).Count());
            Assert.IsTrue(Desc<MusicRhymeSettingsPanel>(window).Any());
            Click(window, "Music.Rhyme.Settings.Cancel");
            Assert.IsFalse(Desc<MusicRhymeSettingsPanel>(window).Any());
        }
        finally { window.Close(); }
    });

    private static TextBox Input(DependencyObject root, string setting) => Desc<TextBox>(root)
        .Single(input => Id(input) == "Music.Rhyme.Setting." + setting);
    private static Slider Range(DependencyObject root, string setting) => Desc<Slider>(root)
        .Single(input => Id(input) == "Music.Rhyme.Setting." + setting);
    private static void Expert(DependencyObject root, bool enabled) => Desc<System.Windows.Controls.CheckBox>(root)
        .Single(check => Id(check) == "Music.Rhyme.Settings.ExpertMode").IsChecked = enabled;
    private static void Click(DependencyObject root, string id) => Desc<Button>(root).Single(button => Id(button) == id)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Id(DependencyObject item) => AutomationProperties.GetAutomationId(item);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
    private static MusicRhymeTuning Tuning(string id, double value) => new() { Values = new(StringComparer.Ordinal) { [id] = value } };
    private static void WithStore(Action<string, MusicRhymePreferences> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-rhyme-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { var path = Path.Combine(directory, "preferences.json"); action(path, new(path)); }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("lopata-rhyme-settings-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    }
}
