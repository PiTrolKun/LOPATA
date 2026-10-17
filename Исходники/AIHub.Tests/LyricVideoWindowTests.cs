using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.LyricVideo;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricVideoWindowTests
{
    [TestMethod, TestCategory("Native")]
    [DataRow("ru", true, 1450d)]
    [DataRow("en", false, 980d)]
    public async Task RealWorkspacePlaysPausesSeeksAndPreviewsCustomSettings(string language, bool dark, double width)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_UI") != "1") Assert.Inconclusive("Opt-in real WPF workspace.");
        var source = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SOURCE") ?? throw new InvalidOperationException();
        var output = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVIDENCE") ?? throw new InvalidOperationException();
        Directory.CreateDirectory(output);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var l = new LocalizationService(); l.Load(language);
            var prefs = Path.Combine(output, "ui-prefs-" + language + ".json");
            var window = new LyricVideoWindow(source, new ClipWork(), l.T, language, new(prefs)) { Width = width };
            Theme(window, dark);
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            window.Loaded += async (_, _) => {
                try
                {
                    await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(90));
                    window.Seek(30); window.Toggle(); await Task.Delay(450);
                    if (window.Time < 30.1) throw new IOException("Playback clock did not advance.");
                    window.Toggle(); var time = window.Time; await Task.Delay(150);
                    if (Math.Abs(time - window.Time) > .03) throw new IOException("Pause did not hold the clock.");
                    window.Seek(272.6); window.Toggle(); await Task.Delay(200); window.Toggle();
                    if (window.Time < 272.7) throw new IOException("Credits clock did not advance.");
                    window.Seek(30);
                    var extra = Desc<TextBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "Music.Video.ExtraRainText");
                    extra.Text = "ЛОПАТА\nНовые миры\nПесни оживают";
                    var palette = Desc<ComboBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "LyricVideo.Palette"); palette.SelectedIndex = 1;
                    var size = Desc<ComboBox>(window).Single(x => AutomationProperties.GetAutomationId(x) == "LyricVideo.TextSize"); size.SelectedIndex = 2;
                    await Task.Delay(700); window.UpdateLayout();
                    var snapshot = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    snapshot.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(snapshot));
                    using (var stream = File.Create(Path.Combine(output, "workspace-" + language + (dark ? "-dark" : "-light") + ".png"))) encoder.Save(stream);
                    var preferences = new ClipPreferences(prefs).Load();
                    if (preferences.Palette != "cyan" || preferences.TextSize != 2 || !preferences.ExtraRainText.Contains("Новые миры"))
                        throw new IOException("Live settings were not persisted.");
                    done.TrySetResult(); window.Close();
                }
                catch (Exception error) { done.TrySetException(error); window.Close(); }
            };
            window.Show(); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Desc<T>(child)) yield return item;
    }
    private static void Theme(FrameworkElement root, bool dark)
    {
        root.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        root.Resources["PanelBrush"] = root.Resources["SecondaryButtonBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        root.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black; root.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DarkSlateGray;
        root.Resources["InputBrush"] = root.Resources["PanelBrush"];
        root.Resources["LineBrush"] = Brushes.SlateGray; root.Resources["AccentBrush"] = Brushes.RoyalBlue;
        root.Resources["UiBodyFontSize"] = 14d;
    }
}
