using System.Drawing;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.ImageLayers;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using SkiaSharp;
using Point = System.Drawing.Point;
using WpfPoint = System.Windows.Point;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageLayerProjectTests
{
    [TestMethod]
    public void SmoothedOutlineStraightensTheDiagonalAndKeepsSquareCorners()
    {
        const int size = 32;
        var diagonal = LayerContours.Trace(DiagonalMask(size), size).Single(ring => !LayerContours.IsHole(ring));
        var smoothed = LayerContourSmoothing.Smooth(diagonal);
        Assert.AreEqual(diagonal.Length, smoothed.Length);
        for (var i = 0; i < diagonal.Length; i++)
        {
            var point = diagonal[i];
            var dx = smoothed[i].X - point.X;
            var dy = smoothed[i].Y - point.Y;
            Assert.IsTrue(dx * dx + dy * dy <= .251);
            if (point.X is >= 8 and <= 24 && point.X - point.Y is 0 or -1)
                Assert.AreEqual(-.5f, smoothed[i].X - smoothed[i].Y, .001f);
        }

        var rectangle = LayerContours.Trace(LayerMask.Area(size, size, new(2, 3, 8, 6)), size).Single();
        CollectionAssert.AreEqual(Array.ConvertAll(rectangle, point => (PointF)point), LayerContourSmoothing.Smooth(rectangle));
    }

    [STATestMethod]
    public void IsolatedLayerKeepsItsSmoothEdgeAndHoleWhenShiftedAtOneThousandPercent()
    {
        const int size = 32;
        const int viewSize = size * 10 + 32;
        using var image = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        image.Erase(SKColors.OrangeRed);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var source = LayerSource.Load("diagonal.png", png.ToArray());
        var canvas = new LayerCanvas { Tool = LayerCanvasTool.Hand };
        var bounds = new System.Windows.Size(viewSize, viewSize);
        canvas.Measure(bounds);
        canvas.Arrange(new System.Windows.Rect(bounds));
        canvas.ShowSource(source);
        canvas.ShowLayer(Guid.NewGuid(), DiagonalMask(size), null, 8, LayerCanvasView.Light);

        var initial = Render();
        var edge = Enumerable.Range(153, 25).Select(y => EdgeAt(initial, y)).ToArray();
        for (var row = 1; row < edge.Length; row++)
            Assert.AreEqual(1, edge[row] - edge[row - 1], "A diagonal should advance by one screen pixel per row.");
        Assert.IsTrue(Blue(initial, 86, 196) > 200, "The opening must show the background.");
        Assert.IsTrue(Blue(initial, 56, 196) < 80, "The pixels next to the opening must remain inside the cutout.");

        var start = new WpfPoint(200, 200);
        var end = new WpfPoint(202.5, 200);
        Assert.IsTrue(canvas.PressPointer(start, MouseButton.Middle, ModifierKeys.None));
        canvas.MovePointer(end);
        Assert.IsTrue(canvas.ReleasePointer(end, MouseButton.Middle));
        var shifted = Render();
        for (var row = 0; row < edge.Length; row++)
            Assert.IsTrue(EdgeAt(shifted, row + 153) - edge[row] is 2 or 3,
                "The cutout should move by a quarter source pixel.");

        byte[] Render()
        {
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(viewSize, viewSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[viewSize * viewSize * 4];
            bitmap.CopyPixels(pixels, viewSize * 4, 0);
            return pixels;
        }

        int EdgeAt(byte[] pixels, int y) => Enumerable.Range(100, 100).Last(x => Blue(pixels, x, y) < 120);
        byte Blue(byte[] pixels, int x, int y) => pixels[(y * viewSize + x) * 4];
    }

    private static LayerMask DiagonalMask(int size) => LayerMask.Build(size * size, bits =>
    {
        for (var y = 0; y < size; y++)
            for (var x = 0; x < y; x++)
                if (!(x is >= 5 and < 9 && y is >= 16 and < 20))
                    bits[y * size + x] = true;
    });

    [TestMethod]
    public void LocalizedCandidateOwnsItsPixelsWhenAWholeFrameCandidateIsListedFirst()
    {
        var document = NewDocument();
        var selected = Area(document, 1, 1, 3, 3);
        var full = LayerMask.Area(document.Source.Width, document.Source.Height, document.Source.Frame);
        var plan = document.PlanAnalysis(new([
            new("background", null, "Background", full, 1_000_000),
            new("target", null, "Target", selected, 25_000)
        ], []), document.Source.Frame, null);
        Assert.AreEqual(selected.Key, document.Direct(plan.Values.Single(layer => layer.Name == "Target")).Key);
        Assert.AreEqual(full.Difference(selected).Key, document.Direct(plan.Values.Single(layer => layer.Name == "Background")).Key);
    }

    [TestMethod]
    public void AddedAndRemovedPointsAreRestoredWithTheirLayerHistory()
    {
        var document = NewDocument();
        const int step = 2;
        var mask = Area(document, 1, 1, 6, 5);
        document.Commit(document.PlanAnalysis(new([new("piece", null, "Piece", mask)], []), document.Source.Frame, null),
            LayerDocument.AnalyzeKind);
        var layer = document.Layers.Keys.Single();
        var rings = LayerContours.Trace(mask, document.Source.Width);
        var controls = LayerPointLayout.Resolve(mask, step, rings, null);
        var added = LayerContourPoints.Insert(mask, step, rings, controls, new(0, 1));
        var plan = document.PlanBoundary(layer, added.Mask);
        plan[layer] = plan[layer] with { Points = added.Points };
        Assert.IsTrue(document.Commit(plan, added.Kind, layer));
        var addedVersion = document.History[^1].Id;
        controls = LayerPointLayout.Resolve(mask, step, rings, added.Points);
        Assert.Contains(1, controls[0]);

        var removal = LayerContourPoints.Remove(mask, document.Source, step, rings, controls, new HashSet<LayerControlPoint> { new(0, 1), new(0, 2) });
        Assert.AreEqual(mask.Key, removal.Mask.Key, "Joining points on a straight edge preserves its pixels.");
        plan = document.PlanBoundary(layer, removal.Mask, allowEmpty: true);
        plan[layer] = plan[layer] with { Points = removal.Points };
        Assert.IsTrue(document.Commit(plan, removal.Kind, layer));
        var folder = Directory.CreateTempSubdirectory("lopata-layer-points-").FullName;
        try
        {
            var path = Path.Combine(folder, "edit" + LayerProjectArchive.Extension);
            LayerProjectArchive.Save(path, document);
            var reopened = LayerProjectArchive.Open(path);
            var after = LayerPointLayout.Resolve(mask, step, rings, reopened.Layers[layer].Points);
            Assert.IsFalse(after[0].Contains(1));
            Assert.IsFalse(after[0].Contains(2));
            Assert.IsNull(reopened.RefinableVersion(layer));
            Assert.IsTrue(reopened.Commit(reopened.PlanRestore(layer, addedVersion), LayerDocument.RestoreKind, layer));
            CollectionAssert.AreEqual(controls[0], LayerPointLayout.Resolve(mask, step, rings, reopened.Layers[layer].Points)[0]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void DeletingAClosedIslandAndDraggingAcrossTheStartVertexKeepsOtherPieces()
    {
        var document = NewDocument();
        var body = Area(document, 1, 1, 6, 5);
        var island = Area(document, 9, 7, 1, 1);
        var mask = body.Union(island);
        var rings = LayerContours.Trace(mask, document.Source.Width);
        var controls = LayerPointLayout.Resolve(mask, 4, rings, null);
        var isolated = rings.FindIndex(ring => ring.Length == 4);
        var drop = controls[isolated].Select(vertex => new LayerControlPoint(isolated, vertex)).ToHashSet();
        var removal = LayerContourPoints.Remove(mask, document.Source, 4, rings, controls, drop);
        Assert.AreEqual(body.Key, removal.Mask.Key);

        var outer = 1 - isolated;
        var withoutStart = controls[outer][1..];
        var vertex = withoutStart[^1];
        var position = rings[outer][vertex];
        var moved = LayerContours.Warp(mask, document.Source, rings[outer], withoutStart,
            new Dictionary<int, PointF> { [vertex] = new(position.X - 1, position.Y) });
        Assert.IsTrue(moved.Changes(mask).Any);
        Assert.IsTrue(moved[7 * document.Source.Width + 9]);
        Assert.AreEqual(2, LayerContours.Trace(moved, document.Source.Width).Count);
    }

    [STATestMethod]
    public void CanvasSupportsPointGesturesAndPanningWithTheLeftButton()
    {
        var document = NewDocument();
        var layer = Guid.NewGuid();
        const int step = 2;
        var mask = Area(document, 1, 1, 6, 5);
        var canvas = new LayerCanvas { Tool = LayerCanvasTool.Points };
        canvas.ShowSource(document.Source);
        canvas.Measure(new System.Windows.Size(252, 212));
        canvas.Arrange(new System.Windows.Rect(0, 0, 252, 212));
        canvas.ShowLayer(layer, mask, null, step, LayerCanvasView.Original);
        var edits = new List<LayerContourEdit>();
        canvas.ContourEdited += edit =>
        {
            edits.Add(edit);
            canvas.ShowLayer(layer, edit.Mask, edit.Points, step, LayerCanvasView.Original);
            canvas.SelectPoints(edit.Selection ?? []);
        };

        Click(new WpfPoint(56, 36));
        Assert.AreEqual(LayerDocument.AddPointKind, edits.Single().Kind);
        var second = new WpfPoint(76, 36);
        Click(second, ModifierKeys.Control);
        Assert.IsTrue(canvas.PressPointer(second, MouseButton.Left, ModifierKeys.None));
        Assert.IsTrue(canvas.HandleKey(Key.Delete));
        Assert.AreEqual(LayerDocument.RemovePointsKind, edits[^1].Kind);
        var rings = LayerContours.Trace(mask, document.Source.Width);
        var retained = LayerPointLayout.Resolve(mask, step, rings, edits[^1].Points)[0];
        Assert.IsFalse(retained.Contains(1));
        Assert.IsFalse(retained.Contains(2));
        Assert.IsTrue(canvas.PressPointer(new WpfPoint(36, 36), MouseButton.Right, ModifierKeys.None));
        Assert.AreEqual(3, edits.Count);
        Assert.AreNotEqual(mask.Key, edits[^1].Mask.Key);

        var boxStart = new WpfPoint(152, 46);
        var boxEnd = new WpfPoint(160, 126);
        Assert.IsTrue(canvas.PressPointer(boxStart, MouseButton.Left, ModifierKeys.Shift));
        canvas.MovePointer(boxEnd);
        Assert.IsTrue(canvas.ReleasePointer(boxEnd, MouseButton.Left));
        var groupStart = new WpfPoint(156, 76);
        var groupEnd = new WpfPoint(176, 76);
        Assert.IsTrue(canvas.PressPointer(groupStart, MouseButton.Left, ModifierKeys.None));
        canvas.MovePointer(groupEnd);
        Assert.IsTrue(canvas.ReleasePointer(groupEnd, MouseButton.Left));
        Assert.AreEqual(LayerDocument.PointsKind, edits[^1].Kind);
        Assert.IsTrue(edits[^1].Selection is { Length: >= 2 });

        canvas.Tool = LayerCanvasTool.Hand;
        canvas.Zoom(1.5);
        var before = PixelAtLeft();
        Assert.IsTrue(canvas.PressPointer(new WpfPoint(108, 76), MouseButton.Left, ModifierKeys.None));
        canvas.MovePointer(new WpfPoint(208, 76));
        Assert.IsTrue(canvas.ReleasePointer(new WpfPoint(208, 76), MouseButton.Left));
        Assert.IsFalse(before.SequenceEqual(PixelAtLeft()));
        Assert.AreEqual(4, edits.Count);

        void Click(WpfPoint at, ModifierKeys modifiers = ModifierKeys.None)
        {
            _ = canvas.PressPointer(at, MouseButton.Left, modifiers);
            _ = canvas.ReleasePointer(at, MouseButton.Left);
        }

        byte[] PixelAtLeft()
        {
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(252, 212, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixel = new byte[4];
            bitmap.CopyPixels(new System.Windows.Int32Rect(10, 76, 1, 1), pixel, 4, 0);
            return pixel;
        }
    }

    [TestMethod]
    public void CommonBoundaryMovesOnceAndRollbackKeepsLaterIndependentPixels()
    {
        var document = NewDocument();
        document.Commit(document.PlanAnalysis(new([
            new("person", null, "Person", document.Empty),
            new("sleeve", "person", "Sleeve", Area(document, 1, 1, 4, 4)),
            new("hand", "person", "Hand", Area(document, 5, 1, 4, 4))
        ], []), document.Source.Frame, null), LayerDocument.AnalyzeKind);
        var first = document.History.Single().Id;
        var sleeve = document.Layers.Values.Single(layer => layer.Name == "Sleeve").Id;
        var hand = document.Layers.Values.Single(layer => layer.Name == "Hand").Id;
        var person = document.Layers.Values.Single(layer => layer.Name == "Person").Id;
        var silhouette = document.Aggregate(person).Key;
        var sharedPixel = 2 * document.Source.Width + 4;
        var laterPixel = 3 * document.Source.Width + 9;

        Assert.IsTrue(document.Commit(document.PlanBoundary(sleeve, Area(document, 1, 1, 3, 4)),
            LayerDocument.PointsKind, sleeve));
        Assert.IsFalse(document.Aggregate(sleeve)[sharedPixel]);
        Assert.IsTrue(document.Aggregate(hand)[sharedPixel]);
        Assert.AreEqual(silhouette, document.Aggregate(person).Key);
        Assert.AreEqual(document.LayerHistory(sleeve).Last().Id, document.LayerHistory(hand).Last().Id);

        var extended = document.Aggregate(hand).Union(Area(document, 9, 3, 1, 1));
        document.Commit(document.PlanBoundary(hand, extended), LayerDocument.PencilKind, hand);
        document.Commit(document.PlanRestore(sleeve, first), LayerDocument.RestoreKind, sleeve, restored: first);
        Assert.IsTrue(document.Aggregate(sleeve)[sharedPixel]);
        Assert.IsFalse(document.Aggregate(hand)[sharedPixel]);
        Assert.IsTrue(document.Aggregate(hand)[laterPixel]);
        Assert.IsTrue(document.Aggregate(person)[laterPixel]);
        Assert.IsNull(document.RefinableVersion(sleeve));
    }

    [TestMethod]
    public void PartialAnalysisHonorsItsRectangleAndArchiveRestoresTheEntireHistory()
    {
        var document = NewDocument();
        var model = new OutputModelUse("gamma", "Gamma", "test", "layer analysis");
        document.Commit(document.PlanAnalysis(new([new("hair", null, "Hair", Area(document, 2, 1, 5, 4))], [model]),
            document.Source.Frame, null), LayerDocument.AnalyzeKind, models: [model]);
        var layer = document.Layers.Values.Single().Id;
        var previous = document.Aggregate(layer).Key;
        var followUp = new LayerProposal([new("$target", null, "Hair", Area(document, 3, 1, 5, 4))], [model]);
        var plan = document.PlanAnalysis(followUp, document.Source.Frame, layer);
        var selected = new Rectangle(0, 0, 5, 3);
        var partial = document.LimitPlan(plan, selected);
        Assert.AreEqual(previous, document.Aggregate(layer).Key);
        Assert.IsNotNull(document.RefinableVersion(layer));
        Assert.IsTrue(document.Commit(partial, LayerDocument.AnalyzeKind, layer, selected, "Details", [model]));
        Assert.IsFalse(document.Aggregate(layer)[1 * document.Source.Width + 2]);
        Assert.IsTrue(document.Aggregate(layer)[3 * document.Source.Width + 2]);
        Assert.IsFalse(document.Aggregate(layer)[1 * document.Source.Width + 7]);

        var folder = Directory.CreateTempSubdirectory("lopata-image-layer-test-").FullName;
        try
        {
            var file = Path.Combine(folder, "Анализ" + LayerProjectArchive.Extension);
            LayerProjectArchive.Save(file, document);
            using (var archive = ZipFile.OpenRead(file))
            {
                using var manifest = archive.GetEntry("manifest.json")!.Open();
                using var json = JsonDocument.Parse(manifest);
                Assert.AreEqual("LOPATA", json.RootElement.GetProperty("provenance").GetProperty("program").GetString());
                using var original = archive.GetEntry("source/image")!.Open();
                using var buffer = new MemoryStream();
                original.CopyTo(buffer);
                CollectionAssert.AreEqual(document.Source.Original, buffer.ToArray());
            }
            var moved = Path.Combine(folder, "Копия" + LayerProjectArchive.Extension);
            File.Move(file, moved);
            var read = LayerProjectArchive.Open(moved);
            Assert.AreEqual(document.Id, read.Id);
            Assert.AreEqual(document.Aggregate(layer).Key, read.Aggregate(layer).Key);
            Assert.AreEqual(2, read.LayerHistory(layer).Count());
            Assert.AreEqual(model, read.History[0].Models.Single());
            read.Commit(read.PlanRestore(layer, read.History[0].Id), LayerDocument.RestoreKind, layer);
            Assert.AreEqual(previous, read.Aggregate(layer).Key);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void DisconnectedComponentsAndHolesProduceIndependentClosedRings()
    {
        var document = NewDocument();
        var selected = Area(document, 1, 1, 6, 6).Difference(Area(document, 3, 3, 2, 2))
            .Union(Area(document, 8, 1, 2, 2));
        var rings = LayerContours.Trace(selected, document.Source.Width);
        Assert.AreEqual(3, rings.Count);
        foreach (var ring in rings)
        {
            Assert.AreEqual(ring.Length, LayerContours.Handles(ring, 1).Length);
            Assert.IsTrue(LayerContours.Handles(ring, 8).Length < ring.Length);
            for (var i = 0; i < ring.Length; i++)
            {
                var next = ring[(i + 1) % ring.Length];
                Assert.AreEqual(1, Math.Abs(next.X - ring[i].X) + Math.Abs(next.Y - ring[i].Y));
            }
        }
        var outer = rings.MaxBy(ring => ring.Length)!;
        var index = Array.IndexOf(outer, new Point(3, 1));
        var moved = LayerContours.Warp(selected, document.Source, outer, LayerContours.Handles(outer, 2),
            new Dictionary<int, PointF> { [index] = new(3, 0) });
        Assert.AreEqual(3, LayerContours.Trace(moved, document.Source.Width).Count);
        Assert.IsFalse(moved[3 * document.Source.Width + 3]);
        Assert.IsFalse(moved[5 * document.Source.Width + 8]);
        Assert.IsTrue(moved.Changes(selected).Any);
    }

    [TestMethod]
    public void ComparisonPreviewUsesExactMaskCoordinatesInCroppedRows()
    {
        var document = NewDocument();
        var mask = Area(document, 1, 1, 2, 1).Union(Area(document, 6, 3, 2, 1));
        var plan = document.PlanAnalysis(new([new("test", null, "Test", mask)], []), document.Source.Frame, null);
        var crop = new Rectangle(2, 1, 5, 3);
        var pixels = new byte[crop.Width * crop.Height * 4];
        LayerComparisonWindow.Proof(document, plan, crop).CopyPixels(pixels, crop.Width * 4, 0);
        var original = document.Source.Pixels.Take(4).ToArray();
        Assert.IsFalse(pixels[..4].SequenceEqual(original));
        CollectionAssert.AreEqual(original, pixels[4..8]);
        CollectionAssert.AreEqual(original, pixels[^8..^4]);
        Assert.IsFalse(pixels[^4..].SequenceEqual(original));
    }

    [TestMethod]
    public void LayerModelGetsReadableTargetNamesAndCanReturnDetailedContours()
    {
        const string label = "чёрная кошка";
        StringAssert.Contains(LayerAnalysisProtocol.ShapePrompt(label, cropped: true), label);
        StringAssert.Contains(LayerAnalysisProtocol.LocatePrompt(label), label);
        StringAssert.Contains(LayerAnalysisProtocol.MapPrompt(LayerDepth.Parts, label, russian: true), label);
        var polygon = Enumerable.Range(0, 192).Select(index => new[] { index, index % 47 }).ToArray();
        var answer = JsonSerializer.Serialize(new { components = new[] { new { outer = polygon, holes = Array.Empty<int[][]>(), open = false } } });
        Assert.AreEqual(polygon.Length, LayerAnalysisProtocol.ReadShapes(answer).Single().Outer.Length);
        var ring = JsonSerializer.SerializeToElement(LayerAnalysisProtocol.ShapeSchema)
            .GetProperty("properties").GetProperty("components").GetProperty("items").GetProperty("properties").GetProperty("outer");
        Assert.IsFalse(ring.TryGetProperty("maxItems", out _));
        Assert.IsTrue(LayerAnalysisProtocol.NeedsSceneContext([348, 58, 615, 995]));
        Assert.IsFalse(LayerAnalysisProtocol.NeedsSceneContext([312, 90, 412, 970]));
        StringAssert.Contains(LayerAnalysisProtocol.MapPrompt(LayerDepth.Objects, null, russian: true), "one whole object");
        StringAssert.Contains(LayerAnalysisProtocol.ShapePrompt("Персонаж", false, [348, 58, 615, 995]), "[348,58,615,995]");
    }

    [TestMethod]
    public void InvertedCropUsesTheHoleOnlyWhenItMatchesTheLocatedObject()
    {
        var image = new Rectangle(0, 0, 100, 100);
        var crop = new Rectangle(10, 10, 80, 80);
        int[][] corners = [[0, 0], [1000, 0], [1000, 1000], [0, 1000]];
        int[][] subject = [[125, 125], [875, 125], [875, 875], [125, 875]];
        var negative = new LayerRasterPiece(crop, [new(corners, [subject])]);
        var recovered = LayerAnalysisProtocol.RepairInvertedCrop(negative, image, [200, 200, 800, 800]);
        Assert.AreSame(subject, recovered.Components.Single().Outer);
        Assert.AreEqual(0, recovered.Components.Single().Holes.Length);
        Assert.AreSame(negative, LayerAnalysisProtocol.RepairInvertedCrop(negative, image, [100, 100, 900, 900]));
        Assert.AreSame(negative, LayerAnalysisProtocol.RepairInvertedCrop(negative, image, [400, 400, 700, 700]));
    }

    [TestMethod]
    public void ModelCoordinatesKeepTheirCropOriginAndRepairRepeatedIdentifiers()
    {
        const string reply = """
            {"pieces":[{"id":"cup","parent":null,"name":"Задняя кружка","box":[100,100,500,780],"fragment":false},
                       {"id":"cup","parent":null,"name":"Передняя кружка","box":[300,400,900,950],"fragment":false},
                       {"id":"handle","parent":"cup","name":"Ручка","box":[100,150,240,380],"fragment":false}]}
            """;
        var layers = LayerAnalysisProtocol.ReadMap(reply, LayerDepth.Parts);
        Assert.AreEqual(3, layers.Select(layer => layer.Id).Distinct().Count());
        Assert.AreEqual(layers[0].Id, layers[2].Parent);
        Assert.AreEqual(new Rectangle(106, 104, 48, 41),
            LayerAnalysisProtocol.PaddedBox(new(100, 100, 100, 60), [100, 120, 500, 700]));
    }

    private static LayerMask Area(LayerDocument document, int x, int y, int width, int height) =>
        LayerMask.Area(document.Source.Width, document.Source.Height, new(x, y, width, height));

    private static LayerDocument NewDocument()
    {
        using var bitmap = new SKBitmap(11, 9, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Goldenrod);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new(LayerSource.Load("source.png", png.ToArray()));
    }
}
