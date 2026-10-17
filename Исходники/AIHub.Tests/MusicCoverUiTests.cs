using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverUiTests
{
    [TestMethod]
    [DataRow("ru", true, 700d)]
    [DataRow("ru", false, 1050d)]
    [DataRow("en", true, 1050d)]
    [DataRow("en", false, 700d)]
    public Task ChooserAndToolbarAreUsableInBothThemesAndLanguages(string language, bool dark, double width) => ScenarioNavigationTests.Sta(() =>
    {
        var root = MusicOutputTests.Temporary();
        try
        {
            var store = new MusicCoverStore(Path.Combine(root, "covers"));
            var input = new MusicCoverInput(Guid.NewGuid().ToString("N"), "ЛОПАТА", "Ночной кот", "Кот смотрит на звёзды", "jazz", "unused", "unused.gguf");
            var run = store.Create(input); var paths = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var source = Path.Combine(store.RunFolder(input), "source-" + i + ".png");
                using var surface = SKSurface.Create(new SKImageInfo(1024, MusicCoverLayout.Create(input.Artist, input.Title).ImageHeight));
                surface.Canvas.Clear(new SKColor((byte)(30 + i * 45), 60, 120));
                using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(source, png.ToArray());
                var path = Path.Combine(store.RunFolder(input), "cover-" + i + ".jpg"); MusicCoverLayout.Compose(source, path, input.Artist, input.Title); paths.Add(path);
            }
            store.SaveRun(run with { Ready = paths.ToArray(), Prompt = "A cat watching the stars, no text", Stage = "Ready" });
            var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs"));
            using var workspace = new MusicWorkspaceControl(new MusicProjects(Path.Combine(root, "projects")), jobs,
                new MusicOutputPreferences(Path.Combine(root, "output.json")), coverStore: store);
            var localization = new LocalizationService(); localization.Load(language); workspace.Localize(localization.T);
            workspace.Editor.Lyrics = input.Lyrics;
            workspace.Generation.SetOptions(new(input.Title, 1, 30) { Artist = input.Artist });
            var host = new Window { Width = 1200, Height = 800, Content = workspace }; Theme(host, dark);
            host.Show(); host.UpdateLayout();
            var button = workspace.TextActions.CoverButton;
            Assert.AreEqual(System.Windows.Media.Brushes.DarkGreen, button.Background);
            store.SetEnabled(input.Artist, input.Title, false);
            workspace.Generation.SetOptions(workspace.Generation.Options);
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert.IsTrue(store.Read(input.Artist, input.Title).IsEnabled);
            var chooser = new MusicCoverWindow(workspace.Covers, input.Artist, input.Title, localization.T, host.Resources)
                { Owner = host, Width = width, Height = 820 };
            try
            {
                chooser.Show(); chooser.UpdateLayout();
                var buttons = Desc<System.Windows.Controls.Button>(chooser).Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Cover.Variant.")).ToArray();
                Assert.HasCount(4, buttons);
                foreach (var choice in buttons)
                {
                    Assert.IsTrue(choice.ActualWidth > 50 && choice.ActualHeight > 50);
                    var point = choice.TranslatePoint(new Point(), chooser);
                    Assert.IsTrue(point.X >= 0 && point.Y >= 0 && point.X + choice.ActualWidth <= chooser.ActualWidth + 1);
                }
                var prompt = Desc<System.Windows.Controls.TextBox>(chooser).Single(b => AutomationProperties.GetAutomationId(b) == "Music.Cover.Prompt");
                prompt.Text = "A warm room and a cat"; chooser.Refresh(); Assert.AreEqual("A warm room and a cat", prompt.Text);
                buttons[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.IsTrue(Desc<System.Windows.Controls.Button>(chooser).Single(b => AutomationProperties.GetAutomationId(b) == "Music.Cover.Use").IsEnabled);
                if (Environment.GetEnvironmentVariable("LOPATA_COVER_EVIDENCE") is { Length: > 0 } evidence)
                {
                    Directory.CreateDirectory(evidence); chooser.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)chooser.ActualWidth, (int)chooser.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(chooser); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidence, language + "-" + dark + "-" + width + ".png")); encoder.Save(file);
                }
            }
            finally { chooser.Close(); host.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });
    private static IEnumerable<T> Desc<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T result) yield return result;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach (var found in Desc<T>(child)) yield return found;
    }
    private static void Theme(FrameworkElement root, bool dark)
    {
        root.Resources["TextPrimaryBrush"] = dark ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
        root.Resources["TextSecondaryBrush"] = dark ? System.Windows.Media.Brushes.LightGray : System.Windows.Media.Brushes.DarkSlateGray;
        root.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(23, 32, 50) : Colors.White);
        root.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(16, 24, 39) : Colors.WhiteSmoke);
        root.Resources["InputBrush"] = root.Resources["PanelBrush"];
        root.Resources["SecondaryButtonBackgroundBrush"] = root.Resources["PanelBrush"];
        root.Resources["LineBrush"] = System.Windows.Media.Brushes.Gray; root.Resources["AccentBrush"] = System.Windows.Media.Brushes.RoyalBlue;
        root.Resources["UiBodyFontSize"] = 14d;
    }
}
