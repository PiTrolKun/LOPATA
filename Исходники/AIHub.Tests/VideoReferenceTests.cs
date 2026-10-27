using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferenceTests
{
    internal static string Image(VideoPreparationTests.TestFiles files, string name = "original.png")
    {
        var path = files.File(name);
        using var bitmap = new SKBitmap(new SKImageInfo(160, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(90, 160, 200, 100));
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var output = File.Create(path)) png.SaveTo(output);
        PngMetadataWriter.Write(path, new Dictionary<string, string> { ["Author"] = "Original author", ["Comment"] = "Imported original" });
        return path;
    }
    internal static async Task<VideoReferenceFile> Import(VideoPreparationProject p, string source, string directory)
    {
        var file = await VideoReferenceFiles.ImportAsync(source, directory);
        var refs = VideoReferences.Ensure(p); refs.Images.Add(file.Image); refs.SelectedImageId = file.Image.Id; refs.Choice = "import";
        p.Attachments.Add(new(file.Path, file.Image.AttachmentRole)); return file;
    }
    [TestMethod]
    public async Task PortableArchiveAndRecoveryKeepExactOriginalAfterSourcesAreGone()
    {
        using var files = new VideoPreparationTests.TestFiles(); var source = Image(files);
        var bytes = File.ReadAllBytes(source); var p = VideoWorldTests.Root();
        var f = await Import(p, source, files.File("managed"));
        var card = p.World!.Cards[0]; VideoReferences.Bind(p, f.Image.Id, card.Id, "appearance", "Keep the round glasses.");
        p.CurrentStep = "references"; var path = files.File("portable.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        using (var zip = ZipFile.OpenRead(path))
        {
            var json = new StreamReader(zip.GetEntry("project.json")!.Open()).ReadToEnd();
            Assert.IsFalse(json.Contains(files.Root, StringComparison.Ordinal));
            using var input = zip.GetEntry("media/00000.png")!.Open(); using var data = new MemoryStream(); input.CopyTo(data);
            CollectionAssert.AreEqual(bytes, data.ToArray());
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        var reader = new VideoPreparationSession(files.File("recovery")); reader.Load(path);
        reader.Project.References!.DraftNotes = "Keep transparency."; reader.Changed(); reader.Checkpoint();
        File.Delete(source); File.Delete(f.Path);
        var loaded = VideoPreparationArchive.Load(reader.RecoveryPath, files.File("opened"));
        var image = loaded.References!.Images.Single(); var binding = loaded.References.Bindings.Single();
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(VideoReferences.PathFor(loaded, image)));
        Assert.AreEqual("imported-original", image.Origin); Assert.AreEqual(card.Id, binding.CardId);
        Assert.AreEqual("Keep transparency.", loaded.References.DraftNotes); Assert.IsFalse(VideoReferences.NeedsReview(loaded, binding));
    }
    [TestMethod]
    public async Task MultipleLinksAndDescriptionReviewUseIdentityAndDoNotDeleteImages()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root();
        var first = await Import(p, Image(files), files.File("managed"));
        var second = await Import(p, Image(files, "second.png"), files.File("managed"));
        var a = p.World!.Cards[0]; var b = VideoWorld.AddCard(p, "character"); b.Name = "Same name";
        a.Name = b.Name;
        var linkA = VideoReferences.Bind(p, first.Image.Id, a.Id, "style", "Colours");
        var linkB = VideoReferences.Bind(p, first.Image.Id, b.Id, "appearance", "Eyes");
        VideoReferences.Bind(p, second.Image.Id, a.Id, "composition", "Framing");
        Assert.HasCount(3, p.References!.Bindings); Assert.IsFalse(VideoReferences.NeedsReview(p, linkA));
        a.Text.IdeaText += " New palette.";
        Assert.IsTrue(VideoReferences.NeedsReview(p, linkA)); Assert.IsFalse(VideoReferences.NeedsReview(p, linkB));
        VideoReferences.Confirm(p, linkA); Assert.IsFalse(VideoReferences.NeedsReview(p, linkA));
        p.World.Cards.Remove(b); p.World.SelectedCardId = a.Id; Assert.IsTrue(VideoReferences.NeedsReview(p, linkB));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferences.Confirm(p, linkB));
        p.Validate(); p.References.Bindings.Remove(linkA); Assert.IsTrue(File.Exists(first.Path)); Assert.HasCount(2, p.References.Images);
    }
    [TestMethod]
    public async Task InvalidAndCanceledInputsDoNotCreateAnAcceptedFileOrChangeOriginal()
    {
        using var files = new VideoPreparationTests.TestFiles(); var invalid = files.File("fake.png"); File.WriteAllText(invalid, "not an image");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoReferenceFiles.ImportAsync(invalid, files.File("managed")));
        Assert.AreEqual("not an image", File.ReadAllText(invalid));
        var source = Image(files); var hash = SHA256.HashData(File.ReadAllBytes(source)); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoReferenceFiles.ImportAsync(source, files.File("managed"), cancel.Token));
        CollectionAssert.AreEqual(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.IsFalse(Directory.Exists(files.File("managed")) && Directory.EnumerateFiles(files.File("managed")).Any());
    }
    [TestMethod]
    public async Task MissingOrDuplicateAttachmentRolesAndBrokenBindingsAreRejected()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root(); var imported = await Import(p, Image(files), files.File("managed"));
        p.Validate(); var attachment = p.Attachments.Single(); p.Attachments.Clear();
        Assert.ThrowsExactly<InvalidDataException>(p.Validate); p.Attachments.Add(attachment); p.Attachments.Add(attachment);
        Assert.ThrowsExactly<InvalidDataException>(p.Validate); p.Attachments.RemoveAt(1);
        var link = VideoReferences.Bind(p, imported.Image.Id, "", "general", ""); link.ImageId = Guid.NewGuid().ToString("N");
        Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }
    [TestMethod]
    public void LegacySchemaWithoutReferencesIsStillReadable()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root(); var path = files.File("legacy.lopataAIVid");
        VideoPreparationArchive.Save(p, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!; string json;
            using (var input = new StreamReader(entry.Open())) json = input.ReadToEnd();
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!; node["Project"]!.AsObject().Remove("References");
            entry.Delete(); using var output = new StreamWriter(zip.CreateEntry("project.json").Open()); output.Write(node.ToJsonString());
        }
        Assert.IsNull(VideoPreparationArchive.Load(path, files.File("opened")).References);
    }

    [TestMethod]
    public async Task ArchiveChecksumStillRejectsChangedImportedImage()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoWorldTests.Root();
        await Import(p, Image(files), files.File("managed")); var path = files.File("tampered.lopataAIVid");
        VideoPreparationArchive.Save(p, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("media/00000.png")!; byte[] bytes;
            using (var input = entry.Open()) { using var data = new MemoryStream(); input.CopyTo(data); bytes = data.ToArray(); }
            bytes[^1] ^= 1; entry.Delete(); using var output = zip.CreateEntry("media/00000.png").Open(); output.Write(bytes);
        }
        var error = Assert.ThrowsExactly<InvalidDataException>(() => VideoPreparationArchive.Load(path, files.File("opened")));
        Assert.AreEqual("VideoPrep.Checksum", error.Message);
    }

    [TestMethod]
    public async Task PhotoOrientationAndPreviewBoundDoNotRewriteOriginalBytes()
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var bitmap = new SKBitmap(160, 100); using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90); var raw = jpeg.ToArray();
        byte[] exif = [69,120,105,102,0,0, 73,73,42,0,8,0,0,0, 1,0, 18,1,3,0,1,0,0,0,6,0,0,0, 0,0,0,0];
        var source = files.File("portrait.jpg"); using (var output = File.Create(source))
        { output.Write(raw, 0, 2); output.Write([255,225,0,(byte)(exif.Length + 2)]); output.Write(exif); output.Write(raw, 2, raw.Length - 2); }
        var original = File.ReadAllBytes(source); var imported = await VideoReferenceFiles.ImportAsync(source, files.File("managed"));
        Assert.AreEqual(100, imported.Image.Width); Assert.AreEqual(160, imported.Image.Height);
        using var preview = VideoReferenceFiles.Preview(imported.Path);
        Assert.AreEqual(100, preview.Width); Assert.AreEqual(160, preview.Height);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(imported.Path)); CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        using var large = new SKBitmap(1800, 1000); using var largeImage = SKImage.FromBitmap(large);
        using var png = largeImage.Encode(SKEncodedImageFormat.Png, 90);
        using var thumb = SKBitmap.Decode(FileMetadataPreview.Thumbnail(png.ToArray()));
        Assert.IsTrue(thumb.Width <= 512 && thumb.Height <= 512);
    }
}
