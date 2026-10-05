using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicTextActionsTests
{
    [TestMethod]
    public void StressPreservesUnicodeAndRejectsDuplicatesAndInvalidSelection()
    {
        var edit = MusicTextEdits.Accent(new("молоко", 5, 1)); Assert.IsNotNull(edit); Assert.AreEqual("о\u0301", edit.Insert);
        Assert.IsNull(MusicTextEdits.Accent(new("о\u0301", 0, 1)));
        Assert.IsNull(MusicTextEdits.Accent(new("о\u0301", 0, 2)));
        Assert.IsNull(MusicTextEdits.Accent(new("оа", 0, 2)));
        Assert.IsNull(MusicTextEdits.Accent(new("б", 0, 1)));
        Assert.IsNull(MusicTextEdits.Accent(new("о", 0, 0)));
        Assert.IsNull(MusicTextEdits.Accent(new("о", 4, 1)));
        Assert.AreEqual("е\u0308\u0301", MusicTextEdits.Accent(new("е\u0308", 0, 2))!.Insert);
    }

    [TestMethod]
    public void SeparateMarkersKeepLyricsSelectionAndLineEndings()
    {
        foreach (var newline in new[] { "\n", "\r\n", "\r" })
        {
            var text = "[Intro]" + newline + "Текст 🦉 о́";
            var start = text.IndexOf("Текст", StringComparison.Ordinal);
            var edit = MusicTextEdits.Markers(new(text, start, text.Length - start), ["[Verse]", "{Император}", "[Spoken]"]);
            Assert.IsNotNull(edit); Assert.AreEqual(0, edit.RemoveLength);
            Assert.AreEqual("[Verse]" + newline + "{Император}" + newline + "[Spoken]" + newline, edit.Insert);
            var result = text.Insert(edit.Start, edit.Insert);
            Assert.AreEqual("Текст 🦉 о́", result.Substring(edit.SelectionStart, edit.SelectionLength));
        }
        Assert.AreEqual(Environment.NewLine + "[Break]" + Environment.NewLine,
            MusicTextEdits.Markers(new("abcdef", 3, 0), ["[Break]"])!.Insert);
        Assert.IsNull(MusicTextEdits.Markers(new("🦉", 1, 0), ["[Verse]"]));
        Assert.IsNull(MusicTextEdits.Markers(new("a\r\nb", 2, 0), ["[Verse]"]));
        Assert.IsNull(MusicTextEdits.Markers(new("a", 0, 0), ["[Verse]\n[Spoken]"]));
        Assert.AreEqual("(Император)", MusicTextEdits.Marker(" Император ", "()"));
        Assert.AreEqual("{Император}", MusicTextEdits.Marker("Император", "{}"));
        Assert.IsNull(MusicTextEdits.Marker("A\nB")); Assert.IsNull(MusicTextEdits.Marker("[A]"));
    }

    [TestMethod]
    public Task NativeUndoRestoresAccentAndMarkersOneStepAndRecounts() => ScenarioNavigationTests.Sta(() =>
    {
        using var editor = new MusicLyricsEditor();
        var host = new Window { Content = editor, Width = 500, Height = 600 }; Theme(host, true);
        Modal(host, () =>
        {
        var box = Desc<TextBox>(editor).Single(); editor.Lyrics = "молоко\r\nВторая строка 🦉";
        box.IsUndoEnabled = false; box.IsUndoEnabled = true; var original = editor.Lyrics; box.Select(5, 1);
        var selection = editor.CaptureSelection(); Assert.IsTrue(editor.ApplyEdit(selection, MusicTextEdits.Accent(selection)));
        StringAssert.Contains(editor.Lyrics, "молоко\u0301"); Assert.IsTrue(box.CanUndo); box.Undo(); Assert.AreEqual(original, editor.Lyrics);
        box.Select(8, editor.Lyrics.Length - 8); selection = editor.CaptureSelection();
        Assert.IsTrue(editor.ApplyEdit(selection, MusicTextEdits.Markers(selection, ["[Verse]", "[Император]"])));
        Assert.AreEqual("Вторая строка 🦉", box.SelectedText); Assert.IsFalse(editor.CanGenerate);
        var counter = Desc<TextBlock>(editor).Single(x => AutomationProperties.GetAutomationId(x) == "Music.CharacterCount");
        editor.Localize(key => key == "Music.Editor.Characters" ? "{0}" : key);
        Assert.AreEqual(MusicTextBudget.CharacterCount(editor.Lyrics).ToString("N0"), counter.Text);
        box.Undo(); Assert.AreEqual(original, editor.Lyrics);
        var old = editor.CaptureSelection(); editor.Lyrics += "!";
        Assert.IsFalse(editor.ApplyEdit(old, MusicTextEdits.Markers(old, ["[Chorus]"])));
        editor.Lyrics = "Куплет"; box.IsUndoEnabled = false; box.IsUndoEnabled = true; box.Select(6, 0);
        var first = editor.CaptureSelection(); editor.ApplyEdit(first, MusicTextEdits.Markers(first, ["[Break]"]));
        var intermediate = editor.Lyrics; var second = editor.CaptureSelection(); editor.ApplyEdit(second, MusicTextEdits.Markers(second, ["[Outro]"]));
        box.Undo(); Assert.AreEqual(intermediate, editor.Lyrics); box.Undo(); Assert.AreEqual("Куплет", editor.Lyrics);
        });
    });

    [TestMethod]
    public Task PickerApplyCancelProfilesAndCustomTagsInBothLanguagesAndThemes() => ScenarioNavigationTests.Sta(() =>
    {
        var localizer = new LocalizationService();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            localizer.Load(language);
            var profiles = new[] { new MusicPerformer("one", "Император", "", "", "", [], [], "") };
            var performer = new MusicTextMarkerWindow(localizer.T, "Performer", profiles); Theme(performer, dark);
            Desc<ComboBox>(performer).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Text.Brackets").SelectedItem = "{}";
            Modal(performer, () => { Snapshot(performer, "marker-picker-performer-" + language + "-" + dark); Click(performer, "Music.Apply"); });
            CollectionAssert.AreEqual(new[] { "{Император}" }, performer.AcceptedMarkers!.ToArray());
            Assert.AreEqual("{}", performer.SelectedBrackets); Assert.AreEqual("Император", profiles[0].Name);
            var empty = new MusicTextMarkerWindow(localizer.T, "Performer", []); Theme(empty, dark);
            Assert.IsFalse(Desc<Button>(empty).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Apply").IsEnabled);
            Modal(empty, () => Click(empty, "Music.Cancel")); Assert.IsNull(empty.AcceptedMarkers);
            var custom = new MusicTextMarkerWindow(localizer.T, "Cue", []); Theme(custom, dark);
            var choices = Desc<ComboBox>(custom).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Text.Choice");
            choices.SelectedIndex = choices.Items.Count - 1;
            Desc<TextBox>(custom).Single().Text = "four bars, Hammond organ";
            Modal(custom, () => { Snapshot(custom, "marker-picker-cue-" + language + "-" + dark); Click(custom, "Music.Apply"); });
            CollectionAssert.AreEqual(new[] { "[four bars, Hammond organ]" }, custom.AcceptedMarkers!.ToArray());
        }
    });

    [TestMethod]
    public Task PerformerButtonUsesCurrentProfilesPreservesSelectedLyricsAndCancelIsHarmless() => ScenarioNavigationTests.Sta(() =>
    {
        using var workspace = new MusicWorkspaceControl(); var l = new LocalizationService(); l.Load("ru"); workspace.Localize(l.T);
        var wishes = new MusicPreferences(); wishes.Performers.Add(new("one", "Император", "", "", "", [], [], "")); workspace.Wishes.Apply(wishes);
        workspace.Editor.Lyrics = "[Verse]\r\nМой текст";
        var host = new Window { Content = workspace, Width = 1200, Height = 760 }; Theme(host, true);
        Modal(host, () =>
        {
            var box = Desc<TextBox>(workspace.Editor).Single(); box.Select(9, 9); var original = workspace.Editor.Lyrics;
            Choose(true); StringAssert.Contains(workspace.Editor.Lyrics, "{Император}\r\nМой текст");
            Assert.AreEqual("Мой текст", box.SelectedText); box.Undo(); Assert.AreEqual(original, workspace.Editor.Lyrics);
            wishes.Performers[0] = wishes.Performers[0] with { Name = "Другой" }; workspace.Wishes.Apply(wishes);
            box.Select(9, 9); Choose(false); Assert.AreEqual(original, workspace.Editor.Lyrics);
            box.Select(9, 9); Choose(true); StringAssert.Contains(workspace.Editor.Lyrics, "{Другой}\r\nМой текст");
            void Choose(bool apply)
            {
                Exception? failure = null;
                host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var picker = host.OwnedWindows.OfType<MusicTextMarkerWindow>().Single();
                    try
                    {
                        var formats = Desc<ComboBox>(picker).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Text.Brackets");
                        formats.SelectedItem = "{}"; Click(picker, apply ? "Music.Apply" : "Music.Cancel");
                    }
                    catch (Exception e) { failure = e; picker.Close(); }
                }));
                Click(workspace.TextActions, "Music.Text.Performer");
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        });
    });

    [TestMethod]
    public Task ToolbarHasSixIconsWithoutScrollAndFitsShortHeight() => ScenarioNavigationTests.Sta(() =>
    {
        using var workspace = new MusicWorkspaceControl(); var l = new LocalizationService(); l.Load("ru"); workspace.Localize(l.T);
        Theme(workspace, true); var actions = workspace.TextActions;
        Assert.AreEqual(0, Desc<ScrollViewer>(actions).Count());
        var buttons = Desc<Button>(actions).ToArray(); Assert.AreEqual(6, buttons.Length);
        Assert.IsTrue(buttons.All(x => x.Content is Viewbox && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(x))));
        Assert.IsFalse(buttons.Single(x => AutomationProperties.GetAutomationId(x) == "Music.Text.Rhyme").IsEnabled);
        var box = Desc<TextBox>(workspace.Editor).Single(); workspace.Editor.Lyrics = "о"; box.Select(0, 1);
        var host = new Window { Content = workspace, Width = 1200, Height = 760 }; Theme(host, true);
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Modal(host, () =>
        {
            Click(actions, "Music.Text.Accent"); Assert.AreEqual("о\u0301", workspace.Editor.Lyrics); box.Undo(); Assert.AreEqual("о", workspace.Editor.Lyrics);
            host.UpdateLayout();
            foreach (var height in new[] { 288d, 150d })
            {
                actions.Height = height; host.UpdateLayout();
                foreach (var button in buttons)
                {
                    var origin = button.TranslatePoint(new Point(), actions);
                    Assert.IsTrue(button.ActualHeight > 0 && origin.Y >= 0 && origin.Y + button.ActualHeight <= actions.ActualHeight + 1);
                }
            }
            actions.Height = double.NaN;
            host.UpdateLayout(); Snapshot(host, "text-actions-workspace");
        });
    });

    private static void Click(DependencyObject root, string id) => Desc<Button>(root).Single(x => AutomationProperties.GetAutomationId(x) == id)
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var found in Desc<T>(child)) yield return found;
    }
    private static void Modal(Window window, Action action)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -10000; window.ShowInTaskbar = false;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        { try { action(); } catch (Exception e) { failure = e; } finally { window.Close(); } }));
        window.ShowDialog();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
}
