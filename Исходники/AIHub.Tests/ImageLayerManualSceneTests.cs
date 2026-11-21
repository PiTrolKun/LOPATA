using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Controls.ImageLayers;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using SkiaSharp;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Point = System.Windows.Point;
using PixelPoint = System.Drawing.PointF;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Drawing.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLayerManualSceneTests
{
    [TestMethod]
    public Task LassoUsesAllThreeDestinationsAndCombinesOverlappingContours() => ScenarioNavigationTests.Sta(() =>
    {
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var owner = new Window { Width = 1060, Height = 720, ShowInTaskbar = false, ShowActivated = false };
        var localizer = new LocalizationService();
        localizer.Load("ru");
        var scenario = new ImageLayersControl(owner, localizer.T, () => true, null!, () => null!);
        owner.Content = scenario;
        var document = new LayerDocument(Source(620, 420));
        typeof(ImageLayersControl).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scenario, [document, null, null]);
        var canvas = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<LayerCanvas>().Single();
        PixelPoint[][] outlines = [];
        canvas.LassoSelected += paths => outlines = paths;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        Action<Window>? dialogAction = null;
        Exception? dialogError = null;
        timer.Tick += (_, _) =>
        {
            if (dialogAction is not { } action) return;
            var dialog = owner.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.IsVisible);
            if (dialog is null) return;
            dialogAction = null;
            try { action(dialog); }
            catch (Exception error)
            {
                dialogError = error;
                if (dialog.IsVisible) dialog.Close();
            }
        };
        try
        {
            owner.Show();
            owner.UpdateLayout();
            timer.Start();
            Choose("LassoNewLayer");
            Assert.AreEqual(LayerCanvasTool.Lasso, canvas.Tool);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(scenario).OfType<Button>()
                .Any(item => AutomationProperties.GetAutomationId(item) == "ImageLayers.Qwen"));
            Assert.IsTrue(canvas.PressPointer(Screen(2, 2), MouseButton.Left, ModifierKeys.None));
            Assert.IsTrue(canvas.HandleKey(Key.M));
            Assert.IsTrue(Find<CheckBox>(scenario, "LassoMagneticToggle").IsChecked == true);
            Assert.IsTrue(canvas.HandleKey(Key.M));
            Assert.IsFalse(canvas.LassoMagnetic);
            canvas.MovePointer(Screen(180, 2));
            Assert.IsTrue(canvas.ReleasePointer(Screen(180, 2), MouseButton.Left));
            Click(300, 120);
            Assert.IsTrue(canvas.HandleKey(Key.Back));
            Click(180, 160);
            Click(2, 160);
            Invoke(Find<Button>(scenario, "LassoNext"));
            Outline(new(90, 86, 160, 134));
            Assert.IsTrue(canvas.PressPointer(Screen(90, 86), MouseButton.Left, ModifierKeys.None));
            canvas.ReleasePointer(Screen(90, 86), MouseButton.Left);
            Assert.AreEqual(0, document.Layers.Count);
            Assert.IsFalse(canvas.CanCloseLassoContour);
            Assert.IsTrue(canvas.HandleKey(Key.Back));
            Assert.IsTrue(canvas.CanCloseLassoContour);
            Invoke(Find<Button>(scenario, "LassoNext"));
            NameAndFinish("Ручной слой", 1);
            var layer = document.Layers.Values.Single();
            Assert.AreEqual("Ручной слой", layer.Name);
            Assert.AreEqual(LayerDocument.ManualLayerKind, document.History.Single().Kind);
            var expected = Mask(document, new(2, 2, 178, 158)).Union(Mask(document, new(90, 86, 160, 134)))
                .Intersect(document.Source.VisibleMask(CancellationToken.None));
            var actual = document.Direct(layer);
            var extra = actual.Difference(expected);
            var missing = expected.Difference(actual);
            Assert.AreEqual(expected.Key, actual.Key,
                $"Extra {extra.Count} at {extra.Bounds(document.Source.Width)}. Missing {missing.Count} at {missing.Bounds(document.Source.Width)}. " +
                string.Join(" | ", outlines.Select(points => string.Join(" ", points))));
            Assert.AreEqual(0, document.History.Single().Models.Length);
            Assert.IsFalse(canvas.HasLassoSelection);

            Choose("LassoSelectedLayer");
            Outline(new(240, 145, 85, 55));
            Invoke(Find<Button>(scenario, "LassoNext"));
            Outline(new(300, 180, 55, 75));
            Invoke(Find<Button>(scenario, "LassoFinish"));
            WaitForCommit(2);
            expected = expected.Union(Mask(document, new(240, 145, 85, 55))).Union(Mask(document, new(300, 180, 55, 75)));
            Assert.AreEqual(expected.Key, document.Aggregate(layer.Id).Key);
            Assert.AreEqual(layer.Id, document.History[^1].Target);
            Assert.AreEqual(LayerDocument.LassoMergeKind, document.History[^1].Kind);

            Choose("LassoNewLayer");
            Outline(new(380, 100, 70, 100));
            NameAndFinish("Другая часть", 3);
            var second = document.Layers.Values.Single(item => item.Id != layer.Id);
            Choose("LassoIntersectingLayer");
            Outline(new(325, 310, 95, 45));
            Assert.IsTrue(canvas.HandleKey(Key.Enter));
            Pump(() => Find<Button>(scenario, "Lasso").IsEnabled);
            Assert.AreEqual(3, document.History.Count);
            Assert.IsTrue(canvas.HasLassoSelection);
            Outline(new(310, 185, 120, 157));
            dialogAction = dialog =>
            {
                var targets = Find<ListBox>(dialog, "LassoTargets");
                Assert.AreEqual(2, targets.Items.Count);
                StringAssert.Contains(((ListBoxItem)targets.Items[0]).Content.ToString(), "Ручной слой");
                StringAssert.Contains(((ListBoxItem)targets.Items[1]).Content.ToString(), "Другая часть");
                targets.SelectedIndex = 1;
                Invoke(Find<Button>(dialog, "LassoMergeAction"));
            };
            Assert.IsTrue(canvas.HandleKey(Key.Enter));
            WaitForCommit(4);
            var addition = Mask(document, new(325, 310, 95, 45)).Union(Mask(document, new(310, 185, 120, 157)));
            Assert.AreEqual(second.Id, document.History[^1].Target);
            Assert.AreEqual(Mask(document, new(380, 100, 70, 100)).Union(addition).Key, document.Aggregate(second.Id).Key);
            Assert.AreEqual(expected.Difference(addition).Key, document.Aggregate(layer.Id).Key);
            var folder = Directory.CreateTempSubdirectory("lopata-lasso-test-");
            try
            {
                var path = Path.Combine(folder.FullName, "selection.lopataIMGp");
                LayerProjectArchive.Save(path, document);
                var reopened = LayerProjectArchive.Open(path);
                Assert.AreEqual(document.Aggregate(second.Id).Key, reopened.Aggregate(second.Id).Key);
                Assert.AreEqual(LayerDocument.LassoMergeKind, reopened.History[^1].Kind);
                Assert.AreEqual(second.Id, reopened.History[^1].Target);
            }
            finally { folder.Delete(recursive: true); }
            Click(520, 220);
            Assert.IsTrue(canvas.HandleKey(Key.Escape));
            Assert.IsFalse(canvas.HasLassoSelection);
        }
        finally
        {
            timer.Stop();
            foreach (Window dialog in owner.OwnedWindows.Cast<Window>().ToArray()) dialog.Close();
            owner.Close();
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }

        void Outline(Rectangle rectangle)
        {
            Click(rectangle.Left, rectangle.Top);
            Click(rectangle.Right, rectangle.Top);
            Click(rectangle.Right, rectangle.Bottom);
            Click(rectangle.Left, rectangle.Bottom);
        }

        void Click(float x, float y)
        {
            var point = Screen(x, y);
            Assert.IsTrue(canvas.PressPointer(point, MouseButton.Left, ModifierKeys.None));
            Assert.IsTrue(canvas.ReleasePointer(point, MouseButton.Left));
        }
        Point Screen(float x, float y) => (Point)typeof(LayerCanvas)
            .GetMethod("ScreenAt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [new PixelPoint(x + .01f, y + .01f)])!;
        void Choose(string destination)
        {
            dialogAction = dialog =>
            {
                var option = Find<RadioButton>(dialog, destination);
                Assert.IsTrue(option.IsEnabled);
                option.IsChecked = true;
                Invoke(Find<Button>(dialog, "LassoStart"));
            };
            var button = Find<Button>(scenario, "Lasso");
            Assert.IsInstanceOfType<System.Windows.Shapes.Path>(button.Content);
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Invoke(button);
            Assert.IsNull(dialogError, dialogError?.ToString());
            Assert.IsNull(dialogAction);
        }
        void NameAndFinish(string name, int versions)
        {
            dialogAction = dialog =>
            {
                Find<TextBox>(dialog, "ManualLayerName").Text = name;
                Invoke(Find<Button>(dialog, "CreateLayerAction"));
            };
            Assert.IsTrue(canvas.HandleKey(Key.Enter));
            WaitForCommit(versions);
        }
        void WaitForCommit(int versions)
        {
            Pump(() => dialogError is not null || document.History.Count == versions && Find<Button>(scenario, "Lasso").IsEnabled);
            Assert.IsNull(dialogError, dialogError?.ToString());
        }
    });

    [TestMethod]
    public void MagneticLassoFollowsACurvedEdgeAndLeavesFlatRegionsAlone()
    {
        var source = Source(110, 82, bitmap =>
        {
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, x < Edge(y) ? SKColors.Black : SKColors.White);
        });
        var moved = LayerMagneticPath.Snap(source, new(53, 40), 9);
        Assert.IsTrue(Math.Abs(moved.X - Edge(moved.Y)) <= 1);
        var untouched = new PixelPoint(20, 35);
        Assert.AreEqual(untouched, LayerMagneticPath.Snap(source, untouched, 9));
        var start = new PixelPoint(56, 10);
        var end = new PixelPoint(56, 70);
        var curve = LayerMagneticPath.Trace(source, start, end, 8);
        Assert.IsTrue(curve.Length > 1);
        Assert.IsTrue(curve.Count(point => Math.Abs(point.X - Edge(point.Y)) <= 2) > curve.Length * .85);
        Assert.AreEqual(end, curve[^1]);
        Assert.AreEqual(1, LayerMagneticPath.Trace(source, untouched, new(20, 70), 8).Length);

        static int Edge(float y) => 56 + (int)Math.Round(5 * Math.Sin((y - 10) * Math.PI / 60));
    }

    [TestMethod]
    public async Task CompletionWithExistingLayersPreservesThemAndPersistsExactVisibleCoverage()
    {
        var document = new LayerDocument(Source(31, 23));
        var objectMask = Mask(document, new(7, 6, 10, 8));
        document.Commit(document.PlanAdditionalLayer("Object", objectMask), LayerDocument.ManualLayerKind);
        var existing = document.Layers.Values.Single();
        var snapshot = document.History.Single().Id;
        var directory = Directory.CreateTempSubdirectory("lopata-layer-test-").FullName;
        try
        {
            var work = new LayerAnalysisWork(directory);
            var input = new LayerAnalysisInput(document.Id, snapshot, document.Source.Frame, null,
                LayerDepth.Objects, false, false, false, Extra: new(LayerAdditionalMethod.CompleteFrame));
            var state = work.Capture("Layers", document, input);
            var result = await work.RunAsync(state, document, null!, new Progress<LayerAnalysisProgress>(), CancellationToken.None);
            Assert.AreEqual(1, document.History.Count);
            var loaded = work.OpenResult(state).Preview;
            Assert.AreEqual(existing, loaded.Layers[existing.Id]);
            Assert.AreEqual(LayerAdditionalMethods.CompleteFrame, loaded.History[^1].Kind);
            Assert.AreEqual(loaded.FrameBackgroundId, result.Preview.FrameBackgroundId);
            var background = loaded.Direct(loaded.Layers[loaded.FrameBackgroundId]);
            var visible = loaded.Source.VisibleMask(CancellationToken.None);
            Assert.IsFalse(background.Meets(objectMask));
            Assert.AreEqual(visible.Key, objectMask.Union(background).Key);
            CollectionAssert.AreEqual(document.Source.Original, loaded.Source.Original);
            var plan = loaded.PlanFrameBackground(new(loaded.Layers), false, CancellationToken.None);
            Assert.IsFalse(loaded.Commit(plan, LayerAdditionalMethods.CompleteFrame));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task CompletionFromAnUnmarkedImageRunsSceneAnalysisAndBuildsTheBackground()
    {
        var doc = new LayerDocument(Source(31, 23));
        var objectPixels = Mask(doc, new(7, 6, 10, 8));
        var analyzer = new FixtureAnalyzer(new([
            new("object", null, "Umbrella", objectPixels),
            new("background", null, "Background", Mask(doc, doc.Source.Frame))
        ], []));
        var directory = Directory.CreateTempSubdirectory("lopata-layer-test-").FullName;
        try
        {
            var work = new LayerAnalysisWork(directory);
            var input = new LayerAnalysisInput(doc.Id, null, doc.Source.Frame, null, LayerDepth.Objects,
                false, false, false, Extra: new(LayerAdditionalMethod.CompleteFrame));
            var state = work.Capture("Layers", doc, input);
            var result = await work.RunAsync(state, doc, analyzer, new Progress<LayerAnalysisProgress>(), CancellationToken.None);
            Assert.AreEqual(doc.Source.Frame, analyzer.Request!.Area);
            Assert.AreEqual(0, doc.Layers.Count);
            Assert.AreEqual(2, result.Preview.Layers.Count);
            Assert.AreEqual(objectPixels.Key, result.Preview.Direct(result.Preview.Layers.Values
                .Single(layer => layer.Name == "Umbrella")).Key);
            var background = result.Preview.Direct(result.Preview.Layers[result.Preview.FrameBackgroundId]);
            Assert.AreEqual(doc.Source.VisibleMask(CancellationToken.None).Key, objectPixels.Union(background).Key);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void RetiredQwenHistoryReopensWithItsMasksAndCannotRunAgain()
    {
        var root = Directory.CreateTempSubdirectory("lopata-layer-test-").FullName;
        try
        {
            var doc = new LayerDocument(Source(40, 30));
            var area = new Rectangle(6, 5, 15, 12);
            var mask = Mask(doc, area);
            doc.Commit(doc.PlanAdditionalLayer("Qwen fixture", mask), LayerAdditionalMethods.RetiredQwen, area: area);
            var path = Path.Combine(root, "retired.lopataIMGp");
            LayerProjectArchive.Save(path, doc);
            var loaded = LayerProjectArchive.Open(path);
            Assert.AreEqual(LayerAdditionalMethods.RetiredQwen, loaded.History.Single().Kind);
            Assert.AreEqual(mask.Key, loaded.Direct(loaded.Layers.Values.Single()).Key);
            var input = new LayerAnalysisInput(doc.Id, null, area, null, LayerDepth.Objects,
                false, false, false, Extra: new(LayerAdditionalMethod.RetiredQwen));
            Assert.IsFalse(LayerAdditionalAnalysis.Valid(input, doc));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void SceneReviewFlagsMissingAndIncorrectlyGroupedObjects()
    {
        var document = new LayerDocument(Source(48, 16));
        document.Commit(document.PlanAdditionalLayers(new[] {
            ("Joined", Mask(document, new(1, 1, 10, 4)), (LayerMatteReference?)null),
            ("Left fragment", Mask(document, new(14, 1, 4, 4)), (LayerMatteReference?)null),
            ("Right fragment", Mask(document, new(18, 1, 4, 4)), (LayerMatteReference?)null),
            ("Offset", Mask(document, new(34, 1, 5, 5)), (LayerMatteReference?)null)
        }), LayerDocument.AnalyzeKind);
        var sample = new LayerProposal([
            new("first", null, "First", Mask(document, new(1, 1, 4, 4))),
            new("second", null, "Second", Mask(document, new(7, 1, 4, 4))),
            new("whole", null, "Unit", Mask(document, new(14, 1, 8, 4))),
            new("missing", null, "Missing", Mask(document, new(24, 1, 4, 4))),
            new("offset", null, "Offset", Mask(document, new(35, 1, 5, 5)))
        ], []);
        var report = LayerSceneReview.Compare(document, sample, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Missing" }, report.Missing);
        CollectionAssert.AreEqual(new[] { "Unit" }, report.Split);
        CollectionAssert.AreEqual(new[] { "Joined" }, report.Merged);
        CollectionAssert.Contains(report.Boundary, "Offset");
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(item => AutomationProperties.GetAutomationId(item) == "ImageLayers." + name);

    private static void Invoke(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static LayerSource Source(int width, int height, Action<SKBitmap>? paint = null)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.SlateGray);
        bitmap.SetPixel(4, 4, SKColors.Transparent);
        paint?.Invoke(bitmap);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return LayerSource.Load("source.png", png.ToArray());
    }

    private static LayerMask Mask(LayerDocument document, Rectangle box) =>
        LayerMask.Area(document.Source.Width, document.Source.Height, box);

    private sealed class FixtureAnalyzer(LayerProposal proposal) : IImageLayerAnalyzer
    {
        internal LayerAnalysisRequest? Request { get; private set; }

        public Task<LayerProposal> AnalyzeAsync(LayerSource source, LayerAnalysisRequest request,
            IProgress<LayerAnalysisProgress> progress, CancellationToken token,
            LayerAnalysisCheckpoint? checkpoint = null, Action<LayerAnalysisCheckpoint>? save = null)
        {
            Request = request;
            return Task.FromResult(proposal);
        }
    }

    private static void Pump(Func<bool> finished)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!finished() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.IsTrue(finished());
    }
}
