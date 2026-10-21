using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAudioTaskUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task WindowOffersModelCapabilitiesWithoutUntranslatedLabels(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        var l = new LocalizationService(); l.Load(language);
        foreach (var variation in new[] { MusicAceCatalog.Variation, MusicStudioRuntime.Variation, MusicHeartMuLaCatalog.Variation }) {
            var window = new MusicAudioTaskWindow(null, variation, new() { References = [new(Path.GetFullPath("reference.wav"))] }, "Words\nMore words", null, l.T);
            window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
            window.Resources["TextSecondaryBrush"] = Brushes.Gray;
            window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23,32,50)) : Brushes.White;
            window.Resources["InputBrush"] = window.Resources["PanelBrush"];
            window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
            window.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16,24,39)) : Brushes.WhiteSmoke;
            window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue; window.Resources["UiBodyFontSize"] = 14d;
            try {
                window.Show(); window.UpdateLayout();
                var elements = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
                Assert.IsFalse(elements.OfType<TextBlock>().Any(t => t.Text.Contains("Music.Transform.")));
                var sourceWords = elements.OfType<System.Windows.Controls.TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.SourceLyrics");
                Assert.IsTrue(sourceWords.IsVisible); sourceWords.Text = "Оригинальные слова / Source words";
                var targetWords = elements.OfType<System.Windows.Controls.TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.Lyrics");
                Assert.AreEqual("Words\nMore words", targetWords.Text);
                Assert.IsTrue(elements.OfType<System.Windows.Controls.Button>().Any(b => AutomationProperties.GetAutomationId(b) == "Music.Transform.OpenSourceText"));
                var usage = elements.OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.InputUsage");
                Assert.AreEqual(l.T("Music.Transform.UsageOff"), usage.Text);
                var tabs = elements.OfType<TabControl>().Single(); Assert.AreEqual(3, tabs.Items.Count);
                var referenceControls = elements.OfType<StackPanel>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.ReferenceControls");
                var referenceRole = elements.OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.ReferenceRole");
                var referenceEnabled = elements.OfType<System.Windows.Controls.CheckBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Transform.Reference.Enabled");
                foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.IsEnabled)) {
                    tabs.SelectedItem = tab; window.UpdateLayout();
                    var mode = tabs.SelectedIndex == 1 ? MusicAudioTaskMode.NewSong : tabs.SelectedIndex == 0 ? MusicAudioTaskMode.Cover
                        : variation == MusicAceCatalog.Variation ? MusicAudioTaskMode.Repaint : MusicAudioTaskMode.Restore;
                    Assert.AreEqual(MusicAudioInputPolicy.AllowsReferences(variation, mode), referenceControls.IsEnabled);
                    Assert.AreEqual(l.T(MusicAudioInputPolicy.ReferenceHintKey(variation, mode)), referenceRole.Text);
                    Assert.IsTrue(referenceEnabled.IsChecked == true, "Changing modes must preserve reference choices.");
                }
                Assert.IsTrue(elements.OfType<System.Windows.Controls.Button>().Any(b => AutomationProperties.GetAutomationId(b) == "Music.Transform.RecognizeLyrics"));
                var directory = Path.Combine(AppContext.BaseDirectory, "music-cover-ui"); Directory.CreateDirectory(directory);
                tabs.SelectedIndex = 0; window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96,96,PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, language + "-" + variation + ".png")); encoder.Save(stream);
                referenceRole.BringIntoView(); window.UpdateLayout();
                var referencesBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96,96,PixelFormats.Pbgra32); referencesBitmap.Render(window);
                var referencesEncoder = new PngBitmapEncoder(); referencesEncoder.Frames.Add(BitmapFrame.Create(referencesBitmap));
                using var referencesStream = File.Create(Path.Combine(directory, language + "-" + variation + "-references.png")); referencesEncoder.Save(referencesStream);
            } finally { window.Close(); }
        }
    });
}
