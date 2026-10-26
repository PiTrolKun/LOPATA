using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.VideoEditor;
using AIHub.Services.VideoEditor;
using Point = System.Windows.Point;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoSelectionUiTests
{
    [TestMethod]
    public async Task BoxSelectionScrollsBothAxesAtStationaryEdgeAndStopsOnCancel()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var p = VideoSelectionTests.Fixture();
                for (var i = 0; i < 14; i++) p.Tracks.Add(new() { Kind = "video", Name = "Extra", Clips = [new() { AssetId = p.Assets[0].Id, Start = 15, Duration = 10 }] });
                var timeline = new VideoTimeline { Project = p, Snap = false }; timeline.Refresh();
                var scroll = new ScrollViewer { Content = timeline, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; timeline.AttachViewport(scroll);
                var owner = new Window { Width = 530, Height = 300, Content = scroll, ShowActivated = false, ShowInTaskbar = false };
                try {
                    owner.Show(); owner.UpdateLayout();
                    var pointer = new Point(scroll.ViewportWidth + 20, scroll.ViewportHeight + 20); timeline.SelectionPointer = _ => pointer;
                    timeline.SelectionDown(new Point(160, 30), ModifierKeys.Alt); timeline.SelectionMove(pointer);
                    await Task.Delay(600);
                    Assert.IsTrue(scroll.HorizontalOffset > 80); Assert.IsTrue(scroll.VerticalOffset > 80);
                    var selected = timeline.Selection.Count; Assert.IsTrue(selected > 0);
                    var forwardX = scroll.HorizontalOffset; var forwardY = scroll.VerticalOffset;
                    pointer = new Point(-20, -20); await Task.Delay(180);
                    Assert.IsTrue(scroll.HorizontalOffset < forwardX); Assert.IsTrue(scroll.VerticalOffset < forwardY);
                    timeline.CancelSelection(); var x = scroll.HorizontalOffset; var y = scroll.VerticalOffset; await Task.Delay(130);
                    Assert.AreEqual(x, scroll.HorizontalOffset); Assert.AreEqual(y, scroll.VerticalOffset); Assert.AreEqual(0, timeline.Selection.Count);
                    var list = new VideoSegmentList { ItemsSource = Enumerable.Range(0, 80).Select(i => "Fragment " + i).ToArray() };
                    owner.Content = list; owner.UpdateLayout();
                    var viewport = FindScroll(list)!; Assert.IsNotNull(viewport);
                    list.SelectionPointer = _ => new Point(viewport.ViewportWidth - 8, viewport.ViewportHeight + 25);
                    list.BeginBox(new Point(350, 8), false); list.UpdateBox(new Point(1, viewport.ViewportHeight + 25)); await Task.Delay(420);
                    Assert.IsTrue(viewport.VerticalOffset > 50); Assert.IsTrue(list.SelectedItems.Count > 10);
                    Assert.IsTrue(list.SelectedItems.Contains(list.Items[0]), "The initial rows stay selected when the box scrolls down");
                    list.EndBox(); var stopped = viewport.VerticalOffset; await Task.Delay(100); Assert.AreEqual(stopped, viewport.VerticalOffset);
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { owner.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is {} found) return found;
        return null;
    }
    [TestMethod]
    public async Task RealTimelineAndListSupportBoxSelectionGroupDragAndSubtitleTrim()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () => {
                var p = VideoSelectionTests.Fixture(); p.Narration.Add(new() { Start = 5, End = 8, Text = "Another language", Language = "en", SubtitleTrackId = "en" });
                var editing = new VideoEditing(p); var timeline = new VideoTimeline { Project = p, Snap = false, Focusable = true, SubtitleLabel = "Субтитры" }; timeline.Refresh();
                timeline.EditRequested += action => { editing.Edit(action); timeline.Project = editing.Project; timeline.Refresh(); };
                var list = new VideoSegmentList { ItemsSource = p.Narration, DisplayMemberPath = "Caption", Height = 240 };
                var panel = new Grid(); panel.ColumnDefinitions.Add(new() { Width = new(850) }); panel.ColumnDefinitions.Add(new() { Width = new(300) });
                var scroll = new ScrollViewer { Content = timeline, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }; timeline.AttachViewport(scroll); panel.Children.Add(scroll); Grid.SetColumn(list, 1); panel.Children.Add(list);
                // These gestures use synthetic model coordinates; edge scrolling has its own injected-pointer test.
                timeline.SelectionPointer = _ => new Point(400, 100);
                var owner = new Window { Width = 1190, Height = 440, Content = panel, ShowActivated = false, ShowInTaskbar = false };
                try {
                    owner.Show(); await Task.Delay(80); owner.UpdateLayout();
                    Assert.AreEqual(7, timeline.Blocks().Length); Assert.AreEqual(2, timeline.Blocks().Where(b => b.Item.Subtitle).Select(b => b.RowId).Distinct().Count());
                    Assert.IsTrue(timeline.ActualHeight >= scroll.ViewportHeight - 1);
                    Assert.IsFalse(timeline.BoxSelectionMode);
                    var empty = new Point(25, timeline.ActualHeight - 5);
                    timeline.SelectionDown(empty, ModifierKeys.None); timeline.SelectionMove(new Point(800, 40));
                    Assert.AreEqual(7, timeline.Selection.Count);
                    await Task.Delay(30);
                    var evidence = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SELECTION_EVIDENCE") ?? Path.GetTempPath(); Directory.CreateDirectory(evidence);
                    var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(owner); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(evidence, "selection-timeline.png"))) png.Save(file);
                    timeline.SelectionUp(new Point(800, 40));
                    var first = p.Tracks[0].Clips[0]; var caption = p.Narration[0]; var firstBlock = timeline.Blocks().Single(b => b.Item == new VideoItemId(first.Id)); var center = new Point(firstBlock.Bounds.Left + 35, firstBlock.Bounds.Top + 20);
                    timeline.SelectionDown(center, ModifierKeys.Control); timeline.SelectionUp(center); Assert.AreEqual(3, timeline.Selection.Count, "Ctrl removes the corresponding group, not only its video root");
                    timeline.SelectItems([new(first.Id), new(p.Tracks[1].Clips[0].Id), new(caption.Id, true)], new(first.Id));
                    timeline.SelectionDown(center, ModifierKeys.None); timeline.SelectionMove(center + new Vector(.5, 0)); timeline.SelectionUp(center + new Vector(.5, 0));
                    Assert.AreEqual(2, first.Start, "Click jitter must not trim or shift a clip");
                    timeline.SelectItems([new(first.Id), new(p.Tracks[1].Clips[0].Id), new(caption.Id, true)], new(first.Id));
                    timeline.SelectionDown(center, ModifierKeys.None); timeline.SelectionMove(center + new Vector(110, 0)); timeline.SelectionUp(center + new Vector(110, 0));
                    Assert.AreEqual(4, first.Start); Assert.AreEqual(4, p.Tracks[1].Clips[0].Start); Assert.AreEqual(10, p.Tracks[0].Clips[1].Start); Assert.AreEqual(4, VideoSubtitles.Cues(p)[0].Start);
                    var cueBlock = timeline.Blocks().Single(b => b.Item == new VideoItemId(caption.Id, true)); var handle = new Point(cueBlock.Bounds.Left + 2, cueBlock.Bounds.Top + 20);
                    timeline.SynchronizeSelection = false;
                    timeline.SelectItems([cueBlock.Item]); timeline.SelectionDown(handle, ModifierKeys.None); timeline.SelectionMove(handle - new Vector(55, 0)); timeline.SelectionUp(handle - new Vector(55, 0));
                    Assert.AreEqual(3, VideoSubtitles.Cues(p)[0].Start); Assert.AreEqual(4, first.Start, "Subtitle trim must not trim the media");
                    list.BeginBox(new Point(290, 170), false); list.UpdateBox(new Point(5, 3)); list.EndBox(); Assert.HasCount(3, list.SelectedItems.Cast<object>().ToArray());
                    timeline.SelectAll(); Assert.AreEqual(7, timeline.Selection.Count); timeline.SelectItems([]); Assert.AreEqual(0, timeline.Selection.Count);
                    p.Validate(); done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { owner.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
}
