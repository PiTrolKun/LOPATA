using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicWishesTests
{
    [TestMethod]
    public void GenreSnapshotSupportsRareNamesAndRussianAndAccentSearch()
    {
        Assert.AreEqual(2208, MusicWishCatalog.Genres.Count);
        Assert.AreEqual(2208, MusicWishCatalog.Genres.Select(x => x.Id).Distinct().Count());
        Assert.IsTrue(MusicWishCatalog.Genres.Any(x => x.Name == "avtorskaya pesnya"));
        Assert.IsTrue(MusicWishCatalog.Genres.Any(x => x.Name == "atmospheric black metal"));
        var l = new LocalizationService(); l.Load("ru");
        Assert.IsTrue(MusicWishCatalog.Matches(MusicWishCatalog.GenreLabel("blues", l.T), "БЛЮЗ"));
        Assert.IsTrue(MusicWishCatalog.Matches("bélé", "bele"));
        Assert.IsTrue(MusicWishCatalog.Matches("hip-hop", "hip hop"));
    }

    [TestMethod]
    public void UnlimitedSelectionCopiesAndPerformerIdentityAreIndependent()
    {
        var state = new MusicPreferences(); state.Select("genres", MusicWishCatalog.Genres.Select(x => x.Name));
        Assert.AreEqual(2208, state.Selected("genres").Count);
        state.Performers.Add(Performer("first", "Император")); var copy = state.Copy();
        copy.Selections["genres"].Clear(); copy.Performers[0] = copy.Performers[0] with { Name = "Другой" };
        Assert.AreEqual(2208, state.Selected("genres").Count); Assert.AreEqual("Император", state.Performers[0].Name);
        Assert.IsFalse(MusicPreferences.ValidPerformerName(" император ", state.Performers));
        Assert.IsFalse(MusicPreferences.ValidPerformerName(" ", state.Performers));
        Assert.IsTrue(MusicPreferences.ValidPerformerName("Новое имя", state.Performers, "first"));
        Assert.AreEqual("first", copy.Performers[0].Id);
    }

    [TestMethod]
    public void InstrumentSuggestionsCombineGenresWithoutChangingSelections()
    {
        var state = new MusicPreferences(); state.Select("genres", ["electric blues", "ambient techno"]);
        var recommended = MusicWishCatalog.Recommendations(state.Selected("genres"));
        CollectionAssert.Contains(recommended["organ"], "electric blues");
        CollectionAssert.Contains(recommended["synthesizer"], "ambient techno");
        Assert.IsFalse(recommended.ContainsKey("harp")); Assert.AreEqual(0, state.Selected("instruments").Count);
        Assert.AreEqual(0, MusicWishCatalog.Recommendations(["мой выдуманный жанр"]).Count);
    }

    [TestMethod]
    public void WishesReachStyleAndInstrumentalRetainsButOmitsVocalProfiles()
    {
        var state = new MusicPreferences { NoChoir = true, NoBacking = true };
        state.Select("genres", ["blues"]); state.Select("instruments", ["organ"]); state.Performers.Add(Performer("first", "Император"));
        var prompt = MusicWishPrompt.Build(state);
        StringAssert.Contains(prompt, "Hammond organ"); StringAssert.Contains(prompt, "Император:");
        StringAssert.Contains(prompt, "baritone"); StringAssert.Contains(prompt, "no choir");
        state.Instrumental = true; var instrumental = MusicWishPrompt.Build(state);
        StringAssert.Contains(instrumental, "instrumental"); Assert.IsFalse(instrumental.Contains("Император", StringComparison.Ordinal));
        Assert.AreEqual(1, state.Performers.Count); state.Instrumental = false; Assert.AreEqual(prompt, MusicWishPrompt.Build(state));
        var tokenizer = new CharacterTokenizer(); var before = MusicTextBudget.Measure(tokenizer, "Текст песни");
        var after = MusicTextBudget.Measure(tokenizer, "Текст песни", prompt);
        Assert.IsTrue(after.TotalTokens > before.TotalTokens); Assert.IsTrue(after.LyricsBudget < before.LyricsBudget);
        var withoutLyrics = MusicTextBudget.Measure(tokenizer, new string('a', 30000), instrumental, instrumental: true);
        Assert.IsFalse(withoutLyrics.Exceeded); Assert.IsTrue(MusicTextBudget.CanStart(withoutLyrics, false, "", true));
        Assert.IsFalse(MusicTextBudget.CanStart(before, false, ""));
    }

    [TestMethod]
    public Task SearchRetainsHiddenSelectionsAndCancelDoesNotChangeCaller() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load("ru"); var original = new[] { "blues" };
        var window = new MusicWishSelectionWindow(l.T, "genres", original); Theme(window, true);
        var search = Desc<System.Windows.Controls.TextBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Search");
        var list = Desc<ListBox>(window).Single(); search.Text = "atmospheric black metal";
        Assert.AreEqual(1, list.Items.Count); SetSelected(list.Items[0], true); search.Text = "Блюз";
        Assert.IsTrue(window.SelectedValues.Contains("atmospheric black metal")); Assert.IsTrue(window.SelectedValues.Contains("blues"));
        Modal(window, () => Click(window, "Music.Cancel")); Assert.IsNull(window.AcceptedValues);
        CollectionAssert.AreEqual(new[] { "blues" }, original);
    });

    [TestMethod]
    public Task DialogApplyCustomAndRecommendedThemeAndPerformerValidation() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            l.Load(language);
            var window = new MusicWishSelectionWindow(l.T, "instruments", [], MusicWishCatalog.Recommendations(["blues"])); Theme(window, dark);
            var list = Desc<ListBox>(window).Single(); var organ = list.Items.Cast<object>().Single(x => x.GetType().GetProperty("Id")!.GetValue(x) as string == "organ");
            StringAssert.StartsWith((string)organ.GetType().GetProperty("Label")!.GetValue(organ)!, "★"); SetSelected(organ, true);
            Desc<System.Windows.Controls.TextBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Custom").Text = "glass harmonica";
            Click(window, "Music.Custom.Add");
            Modal(window, () => { list.ScrollIntoView(organ); window.UpdateLayout(); Snapshot(window, "instruments-" + language + "-" + dark); Click(window, "Music.Apply"); }); CollectionAssert.AreEquivalent(new[] { "organ", "glass harmonica" }, window.AcceptedValues!);
            var profile = Performer("stable", "Император"); var edit = new MusicPerformerEditorWindow(l.T, [profile], profile); Theme(edit, dark);
            var name = Desc<System.Windows.Controls.TextBox>(edit).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Performer.Name");
            name.Text = "Новый"; Modal(edit, () => { Snapshot(edit, "performer-" + language + "-" + dark); Click(edit, "Music.Apply"); });
            Assert.AreEqual("stable", edit.Result!.Id); Assert.AreEqual("baritone", edit.Result.Range);
            CollectionAssert.AreEqual(profile.Timbres.ToArray(), edit.Result.Timbres.ToArray());
            Assert.IsTrue(Desc<TextBlock>(edit).All(x => !x.Text.StartsWith("Music.", StringComparison.Ordinal)));
        }
    });

    [TestMethod]
    public Task WorkspaceLocalizeAndInstrumentalTogglePreserveLyricsAndIndependentProfiles() => ScenarioNavigationTests.Sta(() =>
    {
        using var workspace = new MusicWorkspaceControl(); var l = new LocalizationService(); l.Load("ru"); workspace.Localize(l.T);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE")))
        {
            var window = new Window { Width = 1200, Height = 960, Content = workspace };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
            Theme(window, true); Modal(window, () => Snapshot(window, "wishes-icons-ru-True"));
            window.Content = null;
        }
        workspace.Editor.Lyrics = "Слова песни"; var draft = new MusicPreferences(); draft.Performers.Add(Performer("one", "Первый"));
        draft.Performers.Add(Performer("two", "Второй") with { Voice = "female", Range = "soprano" }); workspace.Wishes.Apply(draft);
        Assert.IsNull(workspace.Editor.Usage); Assert.IsFalse(workspace.Editor.CanGenerate);
        l.Load("en"); workspace.Localize(l.T); draft.Instrumental = true; workspace.Wishes.Apply(draft);
        Assert.AreEqual("Слова песни", workspace.Editor.Lyrics); Assert.AreEqual(2, workspace.Wishes.State.Performers.Count);
        var performers = Desc<System.Windows.Controls.Button>(workspace).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Wishes.performers");
        Assert.IsFalse(performers.IsEnabled); draft.Instrumental = false; workspace.Wishes.Apply(draft); Assert.IsTrue(
            Desc<System.Windows.Controls.Button>(workspace).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Wishes.performers").IsEnabled);
        StringAssert.Contains(workspace.Wishes.RequestStyle, "baritone"); StringAssert.Contains(workspace.Wishes.RequestStyle, "soprano");
    });

    private static MusicPerformer Performer(string id, string name) => new(id, name, "male", "baritone", "russian", ["metallic"], ["restrained"], "");
    private static IEnumerable<T> Desc<T>(DependencyObject root) => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
    private static void SetSelected(object row, bool value) => row.GetType().GetProperty("Selected")!.SetValue(row, value);
    private static void Click(Window window, string id) => Desc<System.Windows.Controls.Button>(window).Single(x => AutomationProperties.GetAutomationId(x) == id)
        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void Modal(Window window, Action action)
    {
        window.Left = -10000; window.Top = -10000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowInTaskbar = false;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { try { action(); } catch (Exception e) { failure = e; } finally { if (window.IsVisible) window.Close(); } });
        window.ShowDialog();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Snapshot(Window window, string name)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE"); if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root); window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, name + ".png")); encoder.Save(output);
    }
    private static void Theme(FrameworkElement element, bool dark)
    {
        element.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        element.Resources["TextSecondaryBrush"] = Brushes.Gray;
        element.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        element.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        element.Resources["SecondaryButtonBackgroundBrush"] = element.Resources["PanelBrush"];
        element.Resources["LineBrush"] = Brushes.Gray; element.Resources["AccentBrush"] = Brushes.RoyalBlue;
        element.Resources["UiBodyFontSize"] = 14d;
    }
    private sealed class CharacterTokenizer : IMusicTokenizer
    { public int Count(string text, CancellationToken cancellation = default) => text.Length; }
}
