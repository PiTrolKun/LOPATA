using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OutputProvenanceTests
{
    private string _root = "";
    private Func<IReadOnlyList<string>, CancellationToken, Task>? _oldGate;
    [TestInitialize] public void Setup()
    {
        _root = MusicOutputTests.Temporary();
        _oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
    }
    [TestCleanup] public void Cleanup()
    {
        ComponentLicenseGate.ConfirmAsync = _oldGate;
        Directory.Delete(_root, true);
    }
    private static OutputProvenance Facts => new()
    {
        Scenario = "test", ModelHistory = "recorded", Models = [new("model/a", "Модель 🎵 <&> 日本語", "r1", "description")]
    };
    [TestMethod, DataRow("png", true), DataRow("jpeg", true), DataRow("webp", true), DataRow("webp", false)]
    public async Task ScreenshotSaveTagsEverySourceAndScaledCopy(string format, bool lossless)
    {
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var bytes = Enumerable.Repeat(new byte[] { 20, 40, 100, 128 }, 32 * 24).SelectMany(b => b).ToArray();
                var image = System.Windows.Media.Imaging.BitmapSource.Create(32, 24, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, bytes, 128);
                image.Freeze();
                completion.SetResult(Enum.GetValues<CaptureSource>().SelectMany(source => ScreenshotFiles.Save(
                    new() { Folder = _root, ImageFormat = format, WebPLossless = lossless }, source, image, image)).ToArray());
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var paths = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.HasCount(8, paths);
        foreach (var path in paths)
        {
            using var decoded = SKBitmap.Decode(path); Assert.AreEqual(32, decoded.Width); Assert.AreEqual(24, decoded.Height);
            var tags = await FileMetadataExifTool.ReadAsync(path, default);
            Assert.IsTrue(tags.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Contains("LOPATA", StringComparison.Ordinal)), tags.ToString());
        }
    }
    [TestMethod, DataRow("PNG"), DataRow("JPEG"), DataRow("WEBP")]
    public async Task ImageTagsAreReadableAndLeaveEncodedPixelsIntact(string format)
    {
        var path = Path.Combine(_root, "test." + format.ToLowerInvariant());
        var bytes = FileMetadataTests.Picture(SKColors.Orange, format); File.WriteAllBytes(path, bytes);
        using var original = SKBitmap.Decode(bytes);
        var payloadBefore = Payload(bytes, format);
        OutputImageMetadata.Write(path, format.ToLowerInvariant(), original.Width, original.Height, Facts);
        var after = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(payloadBefore, Payload(after, format));
        using var decoded = SKBitmap.Decode(after);
        CollectionAssert.AreEqual(original.Bytes, decoded.Bytes);
        var tags = await FileMetadataExifTool.ReadAsync(path, default);
        Assert.IsTrue(tags.ToString().Contains("LOPATA", StringComparison.Ordinal), tags.ToString());
        var json = tags.EnumerateObject().Select(p => p.Value).First(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Contains("\"Program\":\"LOPATA\"", StringComparison.Ordinal)).GetString()!;
        var parsed = JsonSerializer.Deserialize<OutputProvenance>(json)!;
        Assert.AreEqual(Facts.Models[0], parsed.Models[0]);
        Assert.IsFalse(tags.EnumerateObject().Any(p => p.Name.EndsWith(":Warning", StringComparison.Ordinal)), tags.ToString());
    }
    [TestMethod]
    public async Task GifRecoveryPreservesFramesAndOriginalCreationFacts()
    {
        var store = new GifFrameStore(Path.Combine(_root, "frames"), true);
        var pixels = new GifPixels(16, 16, Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 256).SelectMany(b => b).ToArray());
        store.Append(pixels, 0); store.Append(pixels, 200);
        store.Manifest.DurationMilliseconds = 400; store.SaveManifest();
        var opened = GifFrameStore.Open(store.DirectoryPath);
        var path = opened.Assemble(Path.Combine(_root, "test.gif"), default);
        var tags = await FileMetadataExifTool.ReadAsync(path, default);
        var facts = JsonSerializer.Deserialize<OutputProvenance>(tags.EnumerateObject().Single(p => p.Name.EndsWith(":Comment", StringComparison.Ordinal)).Value.GetString()!)!;
        Assert.AreEqual(store.Manifest.Provenance!.CreatedUtc, facts.CreatedUtc);
        using var bitmap = SKBitmap.Decode(path); Assert.AreEqual(16, bitmap.Width); Assert.IsTrue(bitmap.GetPixel(0, 0).Red > 200);
        opened.Manifest.Provenance = null;
        var legacy = opened.Assemble(Path.Combine(_root, "legacy.gif"), default);
        var legacyTags = await FileMetadataExifTool.ReadAsync(legacy, default);
        var legacyFacts = JsonSerializer.Deserialize<OutputProvenance>(legacyTags.EnumerateObject().Single(p => p.Name.EndsWith(":Comment", StringComparison.Ordinal)).Value.GetString()!)!;
        Assert.IsNull(legacyFacts.CreatedUtc); StringAssert.Contains(legacyFacts.Scenario, "unknown");
    }
    [TestMethod, DataRow("mp4", false), DataRow("mkv", false), DataRow("webm", false), DataRow("mp4", true), DataRow("mkv", true), DataRow("webm", true)]
    public async Task FinalVideoAndRecoveryHaveEmbeddedProvenance(string format, bool legacy)
    {
        var store = new VideoFrameStore(Path.Combine(_root, "video"), new() { VideoFormat = format, VideoFps = 15, Processing = "cpu" });
        store.Append(new GifPixels(32, 32, Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 1024).SelectMany(b => b).ToArray()), 0);
        store.Manifest.DurationMilliseconds = 200;
        if (legacy) store.Manifest.Provenance = null;
        store.Save();
        var path = Path.Combine(_root, "output." + format);
        await VideoEncoder.AssembleAsync(VideoFrameStore.Open(store.DirectoryPath), path, default);
        var tags = await FileMetadataExifTool.ReadAsync(path, default);
        Assert.IsTrue(tags.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Contains("\"Program\":\"LOPATA\"", StringComparison.Ordinal)), tags.ToString());
        if (legacy) StringAssert.Contains(tags.ToString(), "original-creation-unknown");
        var decoded = await FileMetadataProcess.RunAsync(VideoEncoder.Executable, ["-v", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], default);
        Assert.AreEqual(32 * 32 * 4, decoded.Length); Assert.IsTrue(decoded[2] > 200 && decoded[0] < 40);
    }
    [TestMethod]
    public void OfficeMarksApplicationPreservesAuthorAndDoesNotStampSandbox()
    {
        var path = Path.Combine(_root, "book.docx");
        LiteraryDocxExporter.Export([new(1, "Глава", ["Текст <&> 😀\nВторая строка"])], path, Facts, "Автор");
        using (var doc = WordprocessingDocument.Open(path, false))
        {
            Assert.AreEqual("Автор", doc.PackageProperties.Creator);
            Assert.HasCount(0, new OpenXmlValidator().Validate(doc).ToArray());
            StringAssert.Contains(doc.MainDocumentPart!.Document!.InnerText, "Текст <&> 😀");
        }
        using (var zip = ZipFile.OpenRead(path))
        {
            var props = XDocument.Load(zip.GetEntry("docProps/custom.xml")!.Open());
            Assert.AreEqual(Facts.Models[0], JsonSerializer.Deserialize<OutputProvenance>(props.Descendants().Single(e => e.Name.LocalName == "lpwstr").Value)!.Models[0]);
        }
        var sandbox = Path.Combine(_root, "sandbox.docx");
        ExecutorDocxExporter.Export(new() { Markdown = "# Result\nSandbox text" }, sandbox);
        using var original = ZipFile.OpenRead(sandbox); Assert.IsNull(original.GetEntry("docProps/custom.xml"));
        var image = Path.Combine(_root, "image.docx");
        ExecutorDocxExporter.Export(new() { Markdown = "# Result\nImage text" }, image, Facts);
        using var tagged = ZipFile.OpenRead(image); Assert.IsNotNull(tagged.GetEntry("docProps/custom.xml"));
    }
    [TestMethod]
    public async Task WaveExportPreservesSamplesAndSource()
    {
        var source = Path.Combine(_root, "source.wav"); MusicOutputTests.WriteWave(source);
        var bytes = File.ReadAllBytes(source); var destination = Path.Combine(_root, "description.wav");
        var before = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(source, default);
        await OutputWaveMetadata.ExportAsync(source, destination, Facts);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        Assert.AreEqual(before, await MusicLyricsTimingMetadata.AudioPayloadHashAsync(destination, default));
        var tags = await FileMetadataExifTool.ReadAsync(destination, default);
        Assert.IsTrue(tags.ToString().Contains("LOPATA", StringComparison.Ordinal));
    }
    [TestMethod]
    public void ModelHistorySurvivesReopenDoesNotGuessLegacyOrUseCurrentSelection()
    {
        Assert.HasCount(0, LiteraryModelHistory.ForProject(_root).Models);
        var first = new OutputModelUse("model/old", "Used", "rev-old", "writer");
        LiteraryModelHistory.Record(_root, first); LiteraryModelHistory.Record(_root, first);
        LiteraryModelHistory.Record(_root, new("model/new", "Used too", "rev-new", "advisor"));
        Assert.HasCount(2, LiteraryModelHistory.ForProject(_root).Models);
        Assert.AreEqual(first, LiteraryModelHistory.ForProject(_root).Models[0]);
        var session = new ImageAnalysisLiterarySession { ModelId = "selected/new", ModelRevision = "new", Versions = [new() { VersionId = "v1", Models = [first] }], SelectedVersionId = "v1" };
        Assert.AreEqual(first, OutputProvenance.ImageDescription(session).Models.Single());
        Assert.Throws<InvalidDataException>(() => (Facts with { Models = [new(@"C:\private\model.gguf", "Bad", "", "")] }).Json());
    }
    [TestMethod]
    public async Task OnlySuccessfulGenerationRequestsRecordTheRuntimeModel()
    {
        var responses = new FakeResponses();
        using var handler = new LiteraryModelHistory.Requests(() => _root) { InnerHandler = responses };
        using var http = new HttpClient(handler);
        using (await http.PostAsync("http://test.invalid/tokenize", new StringContent("{}"))) { }
        Assert.HasCount(0, LiteraryModelHistory.Read(_root));
        responses.Status = HttpStatusCode.BadRequest;
        using (await http.PostAsync("http://test.invalid/v1/chat/completions", new StringContent("{}"))) { }
        Assert.HasCount(0, LiteraryModelHistory.Read(_root));
        responses.Status = HttpStatusCode.OK;
        using (await http.PostAsync("http://test.invalid/v1/chat/completions", new StringContent("{}"))) { }
        Assert.AreEqual(LiteraryModelHistory.MainModel, LiteraryModelHistory.Read(_root).Single());
    }
    private sealed class FakeResponses : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(Status));
    }
    [TestMethod]
    public void ImportAuditKeepsActualModelsAndNewProjectHistoryIsExplicit()
    {
        var input = Path.Combine(_root, "input.json"); File.WriteAllText(input, "[]");
        using var session = ImportSession.Create(_root, input, default);
        var first = session.Add("one/request", "{}").Id;
        session.AddJson("one/settings", new { model = "model/one", modelSha256 = "old-sha" }, first);
        session.AddJson("one/http", new { status = 200 }, first);
        var second = session.Add("two/request", "{}").Id;
        session.AddJson("two/settings", new { model = "model/two", modelSha256 = "new-sha" }, second);
        session.AddJson("two/http", new { status = 200 }, second);
        var failed = session.Add("failed/request", "{}").Id;
        session.AddJson("failed/settings", new { model = "never-used", modelSha256 = "none" }, failed);
        session.AddJson("failed/http", new { status = 400 }, failed);
        var project = Path.Combine(_root, "project"); Directory.CreateDirectory(project);
        LiteraryModelHistory.InitializeNewProject(project);
        Assert.AreEqual("recorded-project", LiteraryModelHistory.ForProject(project).ModelHistory);
        LiteraryModelHistory.CopyImportFacts(session, project);
        LiteraryModelHistory.Record(project, LiteraryModelHistory.Embeddings);
        var output = Path.Combine(_root, "audit.xlsx"); ImportBlackBox.Export(session, output, default, project);
        using var zip = ZipFile.OpenRead(output);
        var xml = XDocument.Load(zip.GetEntry("docProps/custom.xml")!.Open());
        var facts = JsonSerializer.Deserialize<OutputProvenance>(xml.Descendants().Single(e => e.Name.LocalName == "lpwstr").Value)!;
        Assert.HasCount(3, facts.Models); Assert.IsTrue(facts.Models.Any(m => m.Revision == "old-sha"));
        ImportBlackBox.Verify(output, session.State.Artifacts, default);
    }
    [TestMethod]
    public async Task MetadataFailureNeverReplacesAnExistingExport()
    {
        var source = Path.Combine(_root, "not-wave.wav"); File.WriteAllText(source, "invalid");
        var target = Path.Combine(_root, "target.wav"); File.WriteAllText(target, "keep");
        await Assert.ThrowsAsync<EndOfStreamException>(() => OutputWaveMetadata.ExportAsync(source, target, Facts));
        Assert.AreEqual("keep", File.ReadAllText(target)); Assert.HasCount(0, Directory.GetFiles(_root, "*.tmp"));
        var image = Path.Combine(_root, "bad.webp"); File.WriteAllText(image, "not an image"); var original = File.ReadAllBytes(image);
        Assert.Throws<InvalidDataException>(() => OutputImageMetadata.Write(image, "webp", 32, 32, Facts));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(image));
    }
    [TestMethod]
    public void BatchMarkdownAndDocxCarryModelFactsWithoutChangingStory()
    {
        var sections = new[] { (new ImageBatchItem { Position = 1, File = new() { DisplayName = "image.png" } }, new ImageBatchSection("1", "Заголовок", ["Описание 😀"])) };
        ImageBatchExporter.Save(_root, sections, false, "ru", Facts);
        var text = File.ReadAllText(Directory.GetFiles(_root, "*.md").Single());
        Assert.IsTrue(text.StartsWith("---\nlopata_provenance: {", StringComparison.Ordinal)); StringAssert.Contains(text, "Описание 😀");
        ImageBatchExporter.Save(_root, sections, true, "ru", Facts);
        using var doc = WordprocessingDocument.Open(Path.Combine(_root, "Descriptions.docx"), false);
        Assert.HasCount(0, new OpenXmlValidator().Validate(doc).ToArray());
        Assert.IsNotNull(doc.CustomFilePropertiesPart); StringAssert.Contains(doc.MainDocumentPart!.Document!.InnerText, "Описание 😀");
    }
    // Compare compressed image payloads, independently of the production metadata writer.
    private static byte[] Payload(byte[] data, string format)
    {
        if (format == "JPEG")
        {
            var position = 2;
            while (data[position] == 255 && data[position + 1] != 218)
            { var size = (data[position + 2] << 8) | data[position + 3]; position += 2 + size; }
            return data[position..];
        }
        using var input = new MemoryStream(data); using var output = new MemoryStream(); using var reader = new BinaryReader(input);
        input.Position = format == "PNG" ? 8 : 12;
        while (input.Position < input.Length)
        {
            var header = reader.ReadBytes(8);
            var size = format == "PNG" ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header) : BitConverter.ToInt32(header, 4);
            var kind = Encoding.ASCII.GetString(header, format == "PNG" ? 4 : 0, 4);
            var payload = reader.ReadBytes(size);
            if (kind is "IDAT" or "VP8 " or "VP8L" or "ALPH") output.Write(payload);
            input.Position += format == "PNG" ? 4 : size & 1;
        }
        return output.ToArray();
    }
}
