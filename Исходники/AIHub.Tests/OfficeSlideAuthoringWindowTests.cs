using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeSlideAuthoringWindowTests
{
    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public async Task ToolbarEditsSurviveNativeSave(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_NATIVE_SECTION_UI") != "1") Assert.Inconclusive("Requires desktop Office tests.");
        await OfficeProjectUiThread.RunAsync(async () =>
        {
            var folder = Directory.CreateTempSubdirectory("lopata-authoring-");
            var path = Path.Combine(folder.FullName, "slides" + extension);
            var pic = Path.Combine(folder.FullName, "sample.png");
            File.WriteAllBytes(path, OfficeNativeDocument.Create(extension, [new() { Title = "Original", Body = "Body" }]));
            using var bitmap = new SKBitmap(30, 20); bitmap.Erase(SKColors.Orange);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(pic, data.ToArray());
            var loc = new LocalizationService(); loc.Load("ru");
            var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_,_) => Task.CompletedTask;
            var window = new ReaderWindowGroup(null, loc.T, false).OpenEditor(path);
            Button? Btn(string key) => Find<Button>(window).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == key);
            void Click(string id) => Btn(id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Dialog(string command, string value)
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    var modal = window.OwnedWindows.Cast<Window>().Single(w => w.IsVisible);
                    Find<TextBox>(modal).First().Text = value;
                    Find<Button>(modal).Single(b => Equals(b.Content,"OK")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, DispatcherPriority.ApplicationIdle);
                Click("Office.Native." + command);
            }
            try
            {
                await Until(() => Btn("Office.Native.InsertSlideText")?.IsEnabled == true);
                Dialog("InsertSlideText", "Visible authoring marker");
                await Until(() => Find<TextBox>(window).Any(t => t.Text == "Visible authoring marker"));
                Dialog("RotationAngle", "32,5");
                var picker = OfficeSheetWindowTests.CommonDialog(window, pic);
                Click("Office.Native.InsertSlidePicture"); await picker;
                await Until(() => Btn("Office.Native.ReplacePicture")?.IsEnabled == true);
                Click("Office.Native.RotateLeft");
                Click("Office.Native.DeleteObject");
                Click("Office.Editor.Save");
                await Until(() => File.Exists(path+".bak") && Btn("Office.Native.InsertSlideText")?.IsEnabled == true);
                var saved = OfficeNativeDocument.Open(File.ReadAllBytes(path), extension);
                var objects = saved.SlideCanvas(0)!.Objects;
                Assert.AreEqual(3, objects.Count);
                Assert.AreEqual(32.5, objects[^1].Angle!(), .0001);
                Assert.IsTrue(saved.Pages[0].Blocks.Any(b => b.Text == "Visible authoring marker"));
            }
            finally
            {
                var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                discard.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        Find<Button>(dialog).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Close.Discard")
                            ?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                discard.Start();
                try { await window.CloseEditorAsync(); }
                finally { discard.Stop(); ComponentLicenseGate.ConfirmAsync = gate; folder.Delete(true); }
            }
        });
    }

    private static async Task Until(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(55);
        while (!ready()) { if (DateTime.UtcNow > end) Assert.Fail("Slide controls did not reach the expected state."); await Task.Delay(75); }
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root,i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
