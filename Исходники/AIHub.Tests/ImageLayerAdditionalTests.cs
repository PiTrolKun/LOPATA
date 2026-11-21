using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageLayerAdditionalTests
{
    [TestMethod]
    public void RecognizedTextReopensWithWordsLettersCountersAndEditableOrder()
    {
        var document = MakeDocument();
        var coarse = Add(document, "Текст на стене", new(3, 4, 14, 7));
        var hole = LayerMask.Area(32, 24, new(5, 6, 2, 3));
        var letter = LayerMask.Area(32, 24, new(3, 4, 6, 7)).Difference(hole);
        var second = LayerMask.Area(32, 24, new(15, 4, 2, 7));
        var ink = letter.Union(second);
        LayerRecognizedText Text(LayerTextKind kind, string value, int order) => new(kind, value, order, 0, .97);
        var proposal = new LayerProposal(
        [
            new("area", null, "O I", ink, Text: Text(LayerTextKind.Area, "O I", 0)),
            new("word-o", "area", "O", letter, Text: Text(LayerTextKind.Word, "O", 0)),
            new("letter-o", "word-o", "O", letter, Text: Text(LayerTextKind.Glyph, "O", 0)),
            new("word-i", "area", "I", second, Text: Text(LayerTextKind.Word, "I", 1)),
            new("letter-i", "word-i", "I", second, Text: Text(LayerTextKind.Glyph, "I", 0))
        ], []);
        var full = document.PlanAnalysis(proposal, document.Source.Frame, null);
        Assert.IsFalse(document.Commit(document.LimitPlan(full, new(2, 3, 8, 9)), LayerAdditionalMethods.Text));
        Assert.IsTrue(document.Commit(full, LayerAdditionalMethods.Text));
        Assert.IsFalse(document.LayersWithCoverage().Contains(coarse));
        var root = document.Layers.Values.Single(layer => layer.Text?.Kind == LayerTextKind.Area);
        Assert.AreEqual(ink.Key, document.Aggregate(root.Id).Key);
        Assert.IsNull(root.Mask);

        // A later, coarse scene analysis must preserve the already recognized area.
        var scene = new LayerProposal([new("bg", null, "Wall", Area(0, 0, 32, 24)),
            new("text", null, "Text on wall", Area(3, 4, 14, 7))], []);
        Assert.IsTrue(document.Commit(document.PlanAnalysis(scene, document.Source.Frame, null), LayerDocument.AnalyzeKind));
        Assert.IsFalse(document.Layers.Values.Any(layer => layer.Name == "Text on wall"));
        Assert.AreEqual(ink.Key, document.Aggregate(root.Id).Key);
        WithFolder(folder =>
        {
            var path = Path.Combine(folder, "words" + LayerProjectArchive.Extension);
            LayerProjectArchive.Save(path, document);
            using var manifest = ZipFile.OpenRead(path);
            using var json = JsonDocument.Parse(manifest.GetEntry("manifest.json")!.Open());
            Assert.AreEqual(4, json.RootElement.GetProperty("schema").GetInt32());
            var reopened = LayerProjectArchive.Open(path);
            Assert.AreEqual("O I", reopened.Layers[root.Id].Text?.Value);
            var words = reopened.Layers.Values.Where(layer => layer.Parent == root.Id).OrderBy(layer => layer.Text!.Order).ToArray();
            CollectionAssert.AreEqual(new[] { "O", "I" }, words.Select(word => word.Text!.Value).ToArray());
            var glyph = reopened.Layers.Values.Single(layer => layer.Parent == words[0].Id);
            Assert.AreEqual(LayerTextKind.Glyph, glyph.Text?.Kind);
            Assert.HasCount(2, LayerContours.Trace(reopened.Direct(glyph), 32));
            Assert.IsFalse(reopened.Aggregate(root.Id).Meets(hole));
            Assert.IsFalse(reopened.Aggregate(root.Id).Meets(Area(10, 4, 4, 7)));
        });
        LayerMask Area(int x, int y, int width, int height) => LayerMask.Area(32, 24, new(x, y, width, height));
    }

    [TestMethod]
    public void RepeatAnalysisPreservesOmittedLayersAndOnlyTransfersClaimedPixels()
    {
        var document = MakeDocument();
        var omitted = Add(document, "First sign", new(2, 3, 8, 9));
        var unchanged = Add(document, "Second sign", new(18, 4, 7, 9));
        var before = document.Aggregate(omitted);
        var other = document.Aggregate(unchanged);
        var replacement = LayerMask.Area(32, 24, new(8, 9, 4, 3));
        var plan = document.PlanAnalysis(new([new("new", null, "Book", replacement)], []), document.Source.Frame, null);
        Assert.IsTrue(document.Commit(plan, LayerDocument.AnalyzeKind));
        Assert.AreEqual(before.Difference(replacement).Key, document.Aggregate(omitted).Key);
        Assert.AreEqual(other.Key, document.Aggregate(unchanged).Key);
    }

    [TestMethod]
    public void PartsStayInsideTheAssignedPersonAndRespectNamedGarments()
    {
        var crop = new Rectangle(4, 2, 4, 2);
        var frame = LayerMask.Area(12, 6, new(5, 2, 3, 2));
        var scarf = LayerMask.Area(12, 6, new(7, 3, 1, 1));
        byte[] categories = [1, 1, 3, 2, 1, 4, 5, 4];
        var results = LayerPortraitParts.FromLabels(12, 6, crop, categories, frame.Difference(scarf), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1 }, results.Select(result => result.Mask.Count).ToArray());
        var all = results.Select(result => result.Mask).Aggregate((first, second) => first.Union(second));
        Assert.IsFalse(all.Meets(scarf));
        Assert.IsFalse(all[2 * 12 + 4]);
        Assert.IsTrue(results[0].Mask[2 * 12 + 5]);
    }

    [TestMethod]
    public void SoftEdgeReopensAndFollowsTheLayerThroughSelectiveRestore()
    {
        var document = MakeDocument();
        var person = Add(document, "Person", new(4, 4, 10, 10));
        var prop = Add(document, "Prop", new(20, 3, 5, 5));
        var geometry = document.Aggregate(person);
        var matte = LayerMatte.Load(MakePng(14, 14, true));
        var reference = document.AddMatte(matte, geometry, new(2, 2, 14, 14));
        var plan = new Dictionary<Guid, ImageLayer>(document.Layers) { [person] = document.Layers[person] with { Matte = reference } };
        Assert.IsTrue(document.Commit(plan, LayerAdditionalMethods.Matte, person, models: [LayerToolWorker.PyMatting]));
        var cutoutVersion = document.History[^1].Id;

        var changedPerson = LayerMask.Area(32, 24, new(4, 4, 8, 10));
        var changedProp = LayerMask.Area(32, 24, new(20, 3, 4, 5));
        Assert.IsTrue(document.Commit(document.PlanBoundary(person, changedPerson), LayerDocument.PencilKind, person));
        Assert.IsNull(document.ActiveMatte(person));
        Assert.IsTrue(document.Commit(document.PlanBoundary(prop, changedProp), LayerDocument.PencilKind, prop));
        Assert.IsTrue(document.Commit(document.PlanRestore(person, cutoutVersion), LayerDocument.RestoreKind, person,
            restored: cutoutVersion));
        Assert.AreEqual(reference, document.ActiveMatte(person)?.Reference);
        Assert.AreEqual(changedProp.Key, document.Aggregate(prop).Key);

        WithFolder(folder =>
        {
            var path = Path.Combine(folder, "proof.lopataIMGp");
            LayerProjectArchive.Save(path, document);
            using (var zip = ZipFile.OpenRead(path))
            {
                using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                Assert.AreEqual(3, manifest.RootElement.GetProperty("schema").GetInt32());
            }

            var reopened = LayerProjectArchive.Open(path);
            var active = reopened.ActiveMatte(person);
            Assert.IsNotNull(active);
            CollectionAssert.AreEqual(matte.Png, active.Value.Image.Png);
            Assert.AreEqual(changedProp.Key, reopened.Aggregate(prop).Key);

            var png = Path.Combine(folder, "proof.png");
            LayerMatteExport.Save(reopened, person, png, CancellationToken.None);
            var exported = File.ReadAllBytes(png);
            StringAssert.Contains(System.Text.Encoding.Latin1.GetString(exported), "LOPATA");
            StringAssert.Contains(System.Text.Encoding.Latin1.GetString(exported), "image-layers/cutout");
            using var bitmap = SKBitmap.Decode(exported);
            Assert.AreEqual(0, bitmap.GetPixel(0, 0).Alpha);
            Assert.AreEqual(123, bitmap.GetPixel(7, 7).Alpha);
        });
    }

    [TestMethod]
    public void AddingAMarkedObjectTransfersOnlyTheAcceptedPixels()
    {
        var document = MakeDocument();
        var existing = Add(document, "Front", new(2, 2, 13, 12));
        var independent = Add(document, "Away", new(24, 2, 5, 5));
        var unchanged = document.Aggregate(independent);
        var candidate = LayerMask.Area(32, 24, new(8, 2, 11, 12));
        var full = document.PlanAdditionalLayer("Back", candidate);
        var id = full.Values.Single(layer => layer.Name == "Back").Id;
        Assert.AreEqual(7 * 12, document.Direct(document.Layers[existing]).Intersect(candidate).Count);
        var part = new Rectangle(8, 2, 3, 12);
        var partial = document.LimitPlan(full, part);
        Assert.IsTrue(document.Commit(partial, LayerAdditionalMethods.Watershed, area: part, models: [LayerToolWorker.Watershed]));
        Assert.AreEqual(3 * 12, document.Aggregate(id).Count);
        Assert.AreEqual(10 * 12, document.Aggregate(existing).Count);
        Assert.AreEqual(unchanged.Key, document.Aggregate(independent).Key);
    }

    [TestMethod]
    public void MarkerAndDescriptionInputsAreValidatedAgainstTheCapturedArea()
    {
        var document = MakeDocument();
        var area = new Rectangle(4, 4, 10, 10);
        var input = new LayerAnalysisInput(document.Id, null, area, null, LayerDepth.Objects, false, true, false,
            Extra: new(LayerAdditionalMethod.Watershed, Markers: [new(5, 5, true), new(8, 8, false)]));
        Assert.IsTrue(LayerAdditionalAnalysis.Valid(input, document));
        Assert.IsFalse(LayerAdditionalAnalysis.Valid(input with { Area = new(7, 7, 10, 10) }, document));
        Assert.IsFalse(LayerAdditionalAnalysis.Valid(input with { Extra = new(LayerAdditionalMethod.Describe, "\n") }, document));
        Assert.IsTrue(LayerAdditionalAnalysis.Valid(input with { Extra = new(LayerAdditionalMethod.Describe, "red book") }, document));
    }

    [TestMethod]
    public void EdgeDetailsRequireASelectedLayerAndBrushStrokesInsideTheEdit()
    {
        var document = MakeDocument();
        var subject = Add(document, "Subject", new(4, 4, 10, 10));
        var options = new LayerAdditionalOptions(LayerAdditionalMethod.EdgeDetails,
            Strokes: [new(true, [new(12, 6), new(15, 10)]), new(false, [new(9, 11)])]);
        var input = new LayerAnalysisInput(document.Id, document.History[^1].Id, new(8, 4, 8, 9),
            subject, LayerDepth.Objects, false, true, false, Extra: options);

        Assert.IsTrue(LayerAdditionalAnalysis.Valid(input, document));
        Assert.IsFalse(LayerAdditionalAnalysis.Valid(input with { Target = null }, document));
        Assert.IsFalse(LayerAdditionalAnalysis.Valid(input with { Area = new(8, 4, 7, 9) }, document));
        Assert.IsFalse(LayerAdditionalAnalysis.Valid(input with { Extra = options with { Strokes = [new(true, [])] } }, document));
        Assert.AreEqual(LayerAdditionalMethods.EdgeDetails, input.Operation);
    }

    private static LayerDocument MakeDocument() => new(LayerSource.Load("source.png", MakePng(32, 24, false)));

    private static Guid Add(LayerDocument document, string name, Rectangle box)
    {
        var plan = document.PlanAdditionalLayer(name, LayerMask.Area(32, 24, box));
        Assert.IsTrue(document.Commit(plan, LayerDocument.AnalyzeKind));
        return document.Layers.Values.Single(layer => layer.Name == name).Id;
    }

    private static byte[] MakePng(int width, int height, bool translucent)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(translucent ? SKColors.Transparent : SKColors.CornflowerBlue);
        if (translucent) bitmap.SetPixel(width / 2, height / 2, new SKColor(100, 40, 20, 123));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void WithFolder(Action<string> action)
    {
        var folder = Directory.CreateTempSubdirectory("lopata-layer-extra-tests-").FullName;
        try { action(folder); }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
