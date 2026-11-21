using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.ImageLayers;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using SkiaSharp;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using PixelPoint = System.Drawing.PointF;
using PixelRect = System.Drawing.Rectangle;
using Point = System.Windows.Point;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLayerRegionTests
{
    [TestMethod]
    public void RegionEditsRetainOutsideCoverageAndRoundTripThroughLayerHistory()
    {
        var (document, face, other) = Scene();
        var original = document.Aggregate(face);
        var independent = document.Aggregate(other).Key;
        var start = document.History[^1].Id;
        var partial = new PixelRect(8, 5, 2, 3);
        Assert.IsFalse(document.Commit(document.PlanRegionEdit(face, partial, LayerDocument.RemoveRegionContoursKind),
            LayerDocument.RemoveRegionContoursKind, face, partial));

        Apply(LayerDocument.RemoveRegionContoursKind, new(5, 4, 6, 7),
            Area(document, new(2, 2, 12, 12)).Union(Area(document, new(16, 3, 3, 3))));
        var extended = document.Aggregate(face).Union(Area(document, new(12, 9, 5, 3)));
        Apply(LayerDocument.IncludeRegionKind, new(12, 9, 5, 3), extended);
        var cut = new PixelRect(3, 3, 3, 3);
        Apply(LayerDocument.ExcludeRegionKind, cut, extended.Difference(Area(document, cut)));
        var island = new PixelRect(16, 3, 3, 3);
        Apply(LayerDocument.RemoveRegionContoursKind, island, document.Aggregate(face).Difference(Area(document, island)));
        Assert.IsTrue(document.HasManualEdits);
        Assert.IsNull(document.RefinableVersion(face));

        var folder = Directory.CreateTempSubdirectory("lopata-region-proof-").FullName;
        var file = Path.Combine(folder, "edit.lopataIMGp");
        try
        {
            LayerProjectArchive.Save(file, document);
            var reopened = LayerProjectArchive.Open(file);
            CollectionAssert.AreEqual(document.History.Select(step => step.Kind).ToArray(),
                reopened.History.Select(step => step.Kind).ToArray());
            Assert.AreEqual(document.Aggregate(face).Key, reopened.Aggregate(face).Key);
            Assert.IsTrue(reopened.Commit(reopened.PlanRestore(face, start), LayerDocument.RestoreKind, face, restored: start));
            Assert.AreEqual(original.Key, reopened.Aggregate(face).Key);
            Assert.AreEqual(independent, reopened.Aggregate(other).Key);
            CollectionAssert.AreEqual(document.Source.Original, reopened.Source.Original);
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(folder);
        }

        void Apply(string kind, PixelRect rectangle, LayerMask expected)
        {
            var old = document.Aggregate(face);
            Assert.IsTrue(document.Commit(document.PlanRegionEdit(face, rectangle, kind), kind, face, rectangle));
            var next = document.Aggregate(face);
            Assert.AreEqual(expected.Key, next.Key);
            Assert.IsFalse(old.Changes(next).Difference(Area(document, rectangle)).Any);
            Assert.AreEqual(independent, document.Aggregate(other).Key);
            Assert.AreEqual(rectangle, document.History[^1].Area);
        }
    }

    [TestMethod]
    public Task RightClickOpensAreaActionsAndEscapeDismissesTheRegion() => ScenarioNavigationTests.Sta(() =>
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var owner = new Window { Width = 1200, Height = 760, ShowInTaskbar = false, ShowActivated = false };
        var panel = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 32, 51));
        owner.Resources["PanelBrush"] = panel;
        owner.Resources["TextPrimaryBrush"] = System.Windows.Media.Brushes.White;
        owner.Resources["LineBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 55, 75));
        var localizer = new LocalizationService();
        localizer.Load("ru");
        var workspace = new ImageLayersControl(owner, localizer.T, () => true, null!, () => null!);
        owner.Content = workspace;
        var (document, face, _) = Scene();
        typeof(ImageLayersControl).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(workspace, [document, null, face]);
        var canvas = Field<LayerCanvas>("_canvas");
        var scope = Field<System.Windows.Controls.ComboBox>("_scope");
        try
        {
            owner.Show();
            owner.UpdateLayout();
            canvas.Zoom(1.35);
            Find<Button>(workspace, "ImageLayers.RegionTool").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var region = new PixelRect(5, 4, 6, 7);
            var start = OnScreen(5.2f, 4.2f);
            var end = OnScreen(10.8f, 10.8f);
            var center = OnScreen(8, 7);
            Select();

            var outside = OnScreen(13, 10);
            Assert.IsFalse(canvas.PressPointer(outside, MouseButton.Right, ModifierKeys.None));
            Assert.IsFalse(canvas.ReleasePointer(outside, MouseButton.Right));
            Assert.IsNull(Field<ContextMenu?>("_regionActions"));
            Assert.IsTrue(canvas.PressPointer(outside, MouseButton.Left, ModifierKeys.None));
            Assert.IsTrue(canvas.ReleasePointer(outside, MouseButton.Left));
            Assert.AreEqual(region, Field<PixelRect?>("_region"));

            var menu = Open();
            Assert.AreSame(panel, menu.Background);
            CollectionAssert.AreEqual(new[] { "Analyze", "EdgeDetails", "SelectByMarkers", "FindByDescription", "RecognizeText", "CutOutPerson",
                "CreateManualLayer", "IncludeRegion", "ExcludeRegion", "RemoveRegionContours", "ClearRegion" },
                menu.Items.OfType<MenuItem>().Select(item => AutomationProperties.GetAutomationId(item)["ImageLayers.Region.".Length..]).ToArray());
            MenuItem Action(string key) => menu.Items.OfType<MenuItem>()
                .Single(item => AutomationProperties.GetAutomationId(item) == "ImageLayers.Region." + key);
            Action("RemoveRegionContours").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => Field<CancellationTokenSource?>("_operation") is null);
            Assert.AreEqual(LayerDocument.RemoveRegionContoursKind, document.History[^1].Kind);
            Assert.AreEqual(region, document.History[^1].Area);
            Assert.IsTrue(document.Aggregate(face)[6 + 5 * document.Source.Width]);
            Assert.AreEqual(region, Field<PixelRect?>("_region"));

            Assert.IsTrue(canvas.PressPointer(start, MouseButton.Left, ModifierKeys.None));
            canvas.MovePointer(OnScreen(12, 12));
            Assert.IsTrue(Escape().Handled);
            Assert.IsFalse(canvas.ReleasePointer(end, MouseButton.Left));
            Assert.AreEqual(region, Field<PixelRect?>("_region"));
            Assert.IsTrue(Escape().Handled);
            Assert.AreEqual(0, scope.SelectedIndex);
            Assert.IsNull(Field<PixelRect?>("_region"));

            Select();
            menu = Open();
            var count = document.History.Count;
            Action("ClearRegion").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNull(Field<PixelRect?>("_region"));
            Assert.AreEqual(0, scope.SelectedIndex);
            Assert.AreEqual(count, document.History.Count);

            void Select()
            {
                Assert.IsTrue(canvas.PressPointer(start, MouseButton.Left, ModifierKeys.None));
                canvas.MovePointer(end);
                Assert.IsTrue(canvas.ReleasePointer(end, MouseButton.Left));
                Assert.AreEqual(1, scope.SelectedIndex);
                Assert.AreEqual(region, Field<PixelRect?>("_region"));
            }

            ContextMenu Open()
            {
                Assert.IsTrue(canvas.PressPointer(center, MouseButton.Right, ModifierKeys.None));
                Assert.IsTrue(canvas.ReleasePointer(center, MouseButton.Right));
                var popup = Field<ContextMenu>("_regionActions");
                popup.UpdateLayout();
                Assert.IsTrue(popup.IsOpen);
                return popup;
            }

            KeyEventArgs Escape()
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(canvas)!,
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                canvas.RaiseEvent(key);
                return key;
            }
        }
        finally
        {
            if (Field<ContextMenu?>("_regionActions") is { } popup) popup.IsOpen = false;
            owner.Close();
            SynchronizationContext.SetSynchronizationContext(prior);
        }

        T Field<T>(string name) => (T)typeof(ImageLayersControl)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        Point OnScreen(float x, float y) => (Point)typeof(LayerCanvas)
            .GetMethod("ScreenAt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [new PixelPoint(x, y)])!;
    });

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(value => AutomationProperties.GetAutomationId(value) == id);

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.IsTrue(done());
    }

    private static (LayerDocument Document, Guid Face, Guid Other) Scene()
    {
        using var bitmap = new SKBitmap(24, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.SlateGray);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var document = new LayerDocument(LayerSource.Load("source.png", png.ToArray()));
        var face = Area(document, new(2, 2, 12, 12)).Difference(Area(document, new(6, 5, 4, 5)))
            .Union(Area(document, new(7, 7, 1, 1))).Union(Area(document, new(16, 3, 3, 3)));
        var other = Area(document, new(21, 2, 2, 2));
        document.Commit(document.PlanAnalysis(new([new("face", null, "Лицо", face), new("other", null, "Другой слой", other)],
            []), document.Source.Frame, null), LayerDocument.AnalyzeKind);
        return (document, document.Layers.Values.Single(layer => layer.Name == "Лицо").Id,
            document.Layers.Values.Single(layer => layer.Name == "Другой слой").Id);
    }

    private static LayerMask Area(LayerDocument document, PixelRect area) =>
        LayerMask.Area(document.Source.Width, document.Source.Height, area);
}
