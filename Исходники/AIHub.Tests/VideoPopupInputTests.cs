using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services.VideoEditor;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPopupInputTests
{
    [TestMethod]
    public async Task PressReleaseOpensAfterRoutingAndEscapeDismissesPopup()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var p = new VideoProject(); var a = new VideoAsset { Path = System.IO.Path.GetFullPath("tone.wav"), Kind = "audio", Duration = 3, AudioStream = 0 }; p.Assets.Add(a); p.Tracks[1].Clips.Add(new() { AssetId = a.Id, Duration = 3 });
                var timeline = new VideoTimeline { Project = p }; timeline.Refresh();
                var scroll = new System.Windows.Controls.ScrollViewer { Content = timeline, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top };
                var owner = new Window { Width = 700, Height = 370, Content = scroll, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
                var resources = new ResourceDictionary { ["PanelBrush"] = Brushes.DarkSlateGray, ["LineBrush"] = Brushes.SlateGray, ["TextPrimaryBrush"] = Brushes.WhiteSmoke };
                VideoVolumePopup? popup = null; var requests = 0;
                timeline.VolumeRequested += (_, _, at) => { requests++; popup = new VideoVolumePopup(resources, key => key, "Volume", 100, true, "0–200%", _ => {}) { PlacementTarget = timeline, HorizontalOffset = at.X, VerticalOffset = at.Y }; popup.IsOpen = true; };
                try {
                    owner.Show(); await Task.Delay(50);
                    foreach (var clipOnly in new[] { false, true }) {
                        popup = null; var before = requests; var at = clipOnly ? new Point(200, 110) : new Point(60, 135);
                        Assert.IsTrue(timeline.VolumePointer(at, clipOnly, true)); await Task.Delay(40);
                        Assert.IsNull(popup, "The held initiating button must not open a capturing popup");
                        Assert.IsTrue(timeline.VolumePointer(at, clipOnly, false)); Assert.AreEqual(before, requests, "Opening must wait until mouse-up routing has completed"); await Task.Delay(80);
                        Assert.IsNotNull(popup); Assert.IsTrue(popup.IsOpen); Assert.AreEqual(before + 1, requests);
                        Assert.IsFalse(timeline.VolumePointer(new Point(12, 12), clipOnly, false)); await Task.Delay(40);
                        Assert.IsTrue(popup.IsOpen, "A release outside after the completed click must not close the control");
                        popup.Level.Value = 50; Assert.AreEqual(50, popup.Level.Value);
                        Assert.IsFalse(popup.StaysOpen, "Outside-click dismissal uses the WPF popup capture mechanism");
                        popup.Level.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                        await Task.Delay(40); Assert.IsFalse(popup.IsOpen, "Escape from the focused slider must dismiss the popup");
                    }
                    var count = requests; Assert.IsTrue(timeline.VolumePointer(new Point(60, 135), false, true)); Assert.IsTrue(timeline.VolumePointer(new Point(12, 12), false, false)); await Task.Delay(40);
                    Assert.AreEqual(count, requests, "Releasing a canceled click elsewhere must not open the popup");
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { if (popup is not null) popup.IsOpen = false; owner.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
}
