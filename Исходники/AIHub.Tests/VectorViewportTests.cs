using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Controls;
using System.IO;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorViewportTests
{
    [TestMethod]
    public async Task LoadedViewerReplacesPreviewWithNativeSvgFrameAndRefreshesAfterWheelZoom()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "lopata-viewer-" + Guid.NewGuid().ToString("N"));
                using var audit = ComponentAudit.UseTestStore(folder, false);
                var previousGate = ComponentLicenseGate.ConfirmAsync;
                ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
                var surface = new Border();
                var example = VectorExamples.All.Single(x => x.Id == "workshop");
                var viewport = new VectorImageViewport(surface, "fixture", () => VectorExamples.Svg(example), 4d / 3);
                var window = new Window { Content = viewport, Width = 1200, Height = 800, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    for (var i = 0; i < 500 && surface.Visibility != Visibility.Hidden; i++) await Task.Delay(50);
                    Assert.AreEqual(Visibility.Hidden, surface.Visibility);
                    Assert.AreEqual("SVG browser viewport", AutomationProperties.GetHelpText(viewport));
                    var browser = viewport.Children.OfType<VectorBrowserView>().Single();
                    StringAssert.Contains(VectorBrowserView.Document(VectorExamples.Svg(example)), "script-src 'none'");
                    Assert.IsFalse(browser.CoreWebView2.Settings.AreHostObjectsAllowed);
                    var captured = new List<double>();
                    foreach (var scale in new[] { 1d, 2d, 4d, 8d })
                    {
                        var timer = Stopwatch.StartNew();
                        await browser.SetViewportAsync(viewport.ActualWidth, viewport.ActualHeight, 4d / 3,
                            new Matrix(scale, 0, 0, scale, viewport.ActualWidth * (1 - scale) / 2, viewport.ActualHeight * (1 - scale) / 2));
                        using var frame = new MemoryStream();
                        await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, frame);
                        captured.Add(timer.Elapsed.TotalMilliseconds);
                        frame.Position = 0; var bitmap = BitmapFrame.Create(frame, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        Assert.IsTrue(bitmap.PixelWidth > 500); Assert.IsTrue(bitmap.PixelHeight > 300);
                    }
                    Console.WriteLine("SVG browser viewport update + capture ms: " + JsonSerializer.Serialize(captured));
                    await browser.SetViewportAsync(viewport.ActualWidth, viewport.ActualHeight, 4d / 3, Matrix.Identity);
                    await browser.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new WheelEvent('wheel',{deltaY:-100,clientX:300,clientY:200,bubbles:true,cancelable:true}))");
                    Console.WriteLine("After native wheel: " + await browser.CoreWebView2.ExecuteScriptAsync("window.__lopataVector.snapshot()"));
                    for (var i = 0; i < 40 && AutomationProperties.GetItemStatus(viewport) != "120%"; i++) await Task.Delay(25);
                    Assert.AreEqual("120%", AutomationProperties.GetItemStatus(viewport));
                    await browser.SetViewportAsync(viewport.ActualWidth, viewport.ActualHeight, 4d / 3, Matrix.Identity);
                    await browser.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('dblclick',{bubbles:true}))");
                    for (var i = 0; i < 40 && AutomationProperties.GetItemStatus(viewport) != "100%"; i++) await Task.Delay(25);
                    Assert.AreEqual("100%", AutomationProperties.GetItemStatus(viewport));
                    viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120 * 10)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                    for (var i = 0; i < 150 && surface.Visibility != Visibility.Hidden; i++) await Task.Delay(50);
                    Assert.AreEqual(Visibility.Hidden, surface.Visibility);
                    Assert.AreEqual("619%", AutomationProperties.GetItemStatus(viewport));
                    done.SetResult();
                }
                catch (Exception error) { done.SetException(error); }
                finally
                {
                    window.Close(); ComponentLicenseGate.ConfirmAsync = previousGate;
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task WheelZoomIsHandledKeepsProportionsAndResetsWhenViewportResizes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var image = new Border(); var viewport = new VectorImageViewport(image, "fixture");
                viewport.Measure(new System.Windows.Size(400, 300)); viewport.Arrange(new Rect(0, 0, 400, 300));
                viewport.UpdateLayout();
                void Wheel(int delta)
                {
                    var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                    image.RaiseEvent(e); Assert.IsTrue(e.Handled);
                }
                Wheel(120); Assert.AreEqual("120%", AutomationProperties.GetItemStatus(viewport));
                Wheel(-120); Assert.AreEqual("100%", AutomationProperties.GetItemStatus(viewport));
                Wheel(120 * 100); Assert.AreEqual("3200%", AutomationProperties.GetItemStatus(viewport));
                Wheel(-120 * 100); Assert.AreEqual("25%", AutomationProperties.GetItemStatus(viewport));
                var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
                Assert.AreEqual(matrix.M11, matrix.M22); Assert.IsTrue(double.IsFinite(matrix.OffsetX));
                viewport.Measure(new System.Windows.Size(500, 300)); viewport.Arrange(new Rect(0, 0, 500, 300));
                viewport.UpdateLayout();
                Assert.AreEqual("100%", AutomationProperties.GetItemStatus(viewport));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
