using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using Point = System.Windows.Point;

namespace AIHub.Tests;

public sealed partial class OfficeSlideObjectOverlayTests
{
    [TestMethod, DataRow(".pptx"), DataRow(".odp"), TestCategory("Native")]
    public Task DoubleClickEditsSlideTextAndStacksObjects(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SLIDE_OBJECT_WINDOW") != "1") Assert.Inconclusive("Requires desktop pointer.");
        return OfficeProjectUiThread.RunAsync(async () =>
        {
            var file = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite",
                "AI_Office_TestSuite_RU_EN", "04_presentations", "RU", "complex_8_slides" + extension);
            var dir = Directory.CreateTempSubdirectory("lopata-slide-inline-");
            var copy = Path.Combine(dir.FullName, Path.GetFileName(file)); File.Copy(file, copy);
            var slide = OfficeNativeDocument.Open(File.ReadAllBytes(copy), extension).SlideCanvas(3)!;
            var loc = new LocalizationService(); loc.Load("ru");
            var prior = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_,_) => Task.CompletedTask;
            var window = new ReaderWindowGroup(null,loc.T,false).OpenEditor(copy);
            GetPhysicalCursorPos(out var cursor);
            try
            {
                var (canvas,image) = await SelectSlideFour(window);
                var inputTrace = new List<string>();
                window.AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler((_,e) =>
                    inputTrace.Add($"{e.OriginalSource?.GetType().Name}/{e.ClickCount}@{e.GetPosition(canvas)}")),true);
                async Task ClickObject(string name, bool twice)
                {
                    var b = slide.Objects.Single(s => s.Name == name).Bounds;
                    var area = SlideArea(canvas,(BitmapSource)image.Source!);
                    var target = new Point(area.X+(b.X+b.Width*.45)*area.Width/slide.Width,
                        area.Y+(b.Y+b.Height*.50)*area.Height/slide.Height);
                    try { await AimPointer(window,canvas,target); }
                    catch (AssertFailedException) when (Mouse.DirectlyOver == canvas &&
                        canvas.ObjectRect(slide.Objects.Single(s => s.Name == name)).Contains(Mouse.GetPosition(canvas)))
                    { /* A click anywhere inside this object is valid. Verify the owning HWND below. */ }
                    Assert.IsTrue(GetPhysicalCursorPos(out var physical));
                    Assert.AreEqual(new WindowInteropHelper(window).Handle,GetAncestor(WindowFromPhysicalPoint(physical),2));
                    MouseButton(2); MouseButton(4);
                    if (twice) { await Task.Delay(50); MouseButton(2); MouseButton(4); }
                    await Task.Delay(100);
                }
                void Click(string id) => Desc<Button>(window,id)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await ClickObject("TextBox 2",true);
                await Task.Delay(250);
                Assert.IsTrue(Desc<TextBox>(window,"Office.Native.InlineText")?.IsKeyboardFocused == true,
                    $"Selection {canvas.Selected?.Name}, events {string.Join("; ",inputTrace)}");
                Desc<TextBox>(window,"Office.Native.InlineText")!.Text = "Проверка на холсте";
                Click("Office.Native.SendToBack");
                await ClickObject("Picture 4",false);
                Click("Office.Native.BringToFront");
                Click("Office.Editor.Save");
                await Wait(() => File.Exists(copy+".bak") && canvas.IsEnabled,"save");
                var saved = OfficeNativeDocument.Open(File.ReadAllBytes(copy),extension);
                Assert.IsTrue(saved.Pages[3].Blocks.Any(b => b.Text == "Проверка на холсте"));
                Assert.AreEqual("TextBox 2",saved.SlideCanvas(3)!.Objects[0].Name);
                Assert.AreEqual("Picture 4",saved.SlideCanvas(3)!.Objects[^1].Name);
            }
            finally
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_,_) => { foreach(Window w in window.OwnedWindows)
                    Desc<Button>(w,"Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                timer.Start();
                try { await window.CloseEditorAsync(); }
                finally { timer.Stop(); SetPhysicalCursorPos(cursor.X,cursor.Y); ComponentLicenseGate.ConfirmAsync = prior; dir.Delete(true); }
            }
        });
    }
}
