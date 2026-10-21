using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicMidiUiTests
{
    [TestMethod, DataRow("ru",MusicStudioRuntime.Variation), DataRow("en",MusicAceCatalog.Variation), DataRow("en",MusicHeartMuLaCatalog.Variation)]
    public Task EditorAppliesOnlyExplicitlyAndHasLocalizedAccessibleControls(string language,string variation) => ScenarioNavigationTests.Sta(() => {
        var l = new LocalizationService(); l.Load(language); var initial = MusicMidiTests.Tune();
        var window = new MusicMelodyWindow(initial,variation,l.T);
        window.Resources["TextPrimaryBrush"] = language == "ru" ? Brushes.White : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = Brushes.Gray;
        window.Resources["PanelBrush"] = language == "ru" ? new SolidColorBrush(Color.FromRgb(23,32,50)) : Brushes.White;
        window.Resources["InputBrush"] = window.Resources["PanelBrush"];
        window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
        window.Resources["WindowBackgroundBrush"] = language == "ru" ? new SolidColorBrush(Color.FromRgb(16,24,39)) : Brushes.WhiteSmoke;
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            try {
                var elements = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
                Assert.IsFalse(elements.OfType<TextBlock>().Any(t => t.Text.Contains("Music.Midi.")));
                Button Button(string id) => elements.OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="Music.Midi."+id);
                TextBox Input(string id) => elements.OfType<TextBox>().Single(b=>AutomationProperties.GetAutomationId(b)=="Music.Midi."+id);
                Input("Pitch").Text="70"; Input("Start").Text="5"; Input("Length").Text="1";
                Button("SetNote").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.HasCount(4,window.Result.Notes,"Editor changes must not apply before Apply.");
                Button("Undo").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Button("Redo").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.UpdateLayout();
                var image = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                var folder = Path.Combine(AppContext.BaseDirectory,"music-midi"); Directory.CreateDirectory(folder);
                using(var output = File.Create(Path.Combine(folder,language+"-"+variation+".png"))) png.Save(output);
                Button("Apply").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            } catch(Exception e) { failure=e; window.Close(); }
        }));
        Assert.IsTrue(window.ShowDialog()==true); if(failure is not null) throw failure;
        Assert.HasCount(5,window.Result.Notes); Assert.AreEqual(70,window.Result.Notes.Single(n=>n.Start==2400).Pitch);
        Assert.AreEqual(4,initial.Notes.Length); Assert.AreEqual(variation!=MusicHeartMuLaCatalog.Variation,window.Result.Enabled);
    });
}
