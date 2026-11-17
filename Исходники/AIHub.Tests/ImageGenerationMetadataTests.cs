using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageGenerationMetadataTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-metadata-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ImageGenerationRequest Request(string model = "z-image") => new(Guid.NewGuid().ToString("N"), model,
        "  Замок <на скале> & кот 🐈\nDo not rewrite this.  ", 256, 256, [123, 456], "unused-models-root", ImageGenerationSessionStore.Create(_root))
        { Metadata = new("  Тестовый автор 🐈  "), SubmittedAt = new DateTimeOffset(2026, 10, 4, 6, 7, 8, TimeSpan.FromHours(7)),
            FirstGenerationNumber = 7, OutputFolder = Path.Combine(_root, "export") };

    [TestMethod]
    [DataRow(ImageGenerationFileFormat.Png)]
    [DataRow(ImageGenerationFileFormat.Jpeg)]
    [DataRow(ImageGenerationFileFormat.WebP)]
    public async Task SenseNovaExportsWithItsOwnBackendAndModelFiles(ImageGenerationFileFormat format)
    {
        var request = Request(VisualSenseNova.Id) with { Seeds = [123], OutputFormat = format, EngineSetId = "002" };
        var worker = new Worker { Receipt = VisualSenseNova.RenderReceipt() };
        var turn = await new ImageGenerationRuntime(worker).RunAsync(request, [], null, default);
        var result = turn.Results.Single();

        Assert.IsTrue(result.Exported, result.ProcessingError ?? result.ExportError);
        Assert.IsTrue(ImageGenerationOutput.IsReady(request, 0));
        Assert.AreEqual(1, worker.Calls);
        AssertSenseNovaMetadata(request);
    }

    [TestMethod]
    public async Task SenseNovaFinalizesStagedPixelsFromLegacyReceiptWithoutDrawingAgain()
    {
        var request = Request(VisualSenseNova.Id) with { Seeds = [123], EngineSetId = "002" };
        var partial = Path.Combine(request.SessionDirectory, request.Id + "_0.pending.png");
        var writer = new Worker();
        await writer.GenerateAsync(request, 0, [], "", partial, default);
        await File.WriteAllTextAsync(Path.Combine(request.SessionDirectory, request.Id + "_0.runtime.json"),
            JsonSerializer.Serialize(VisualSenseNova.RenderReceipt() with { EngineSetId = "managed-python" }));
        var worker = new Worker { FailAt = 0 };
        var runtime = new ImageGenerationRuntime(worker);

        var turn = await runtime.RunAsync(request, [], null, default);
        var resumed = await runtime.RunAsync(request, [], null, default);

        Assert.AreEqual(0, worker.Calls);
        Assert.IsFalse(File.Exists(partial));
        Assert.IsTrue(turn.Results.Single().Exported);
        Assert.AreEqual(turn.Results.Single().ExportPath, resumed.Results.Single().ExportPath);
        Assert.HasCount(1, Directory.GetFiles(request.OutputFolder));
        Assert.IsNull(turn.Results.Single().Runtime!.EngineSetId);
        var actual = File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0));
        CollectionAssert.AreEqual(Chunks(writer.Bytes[0], "IDAT").SelectMany(bytes => bytes).ToArray(),
            Chunks(actual, "IDAT").SelectMany(bytes => bytes).ToArray());
        AssertSenseNovaMetadata(request);
    }

    [TestMethod]
    public async Task SavedSenseNovaFrameResumesThroughNormalExport()
    {
        var fixture = Environment.GetEnvironmentVariable("LOPATA_SENSENOVA_EXPORT_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) Assert.Inconclusive("Opt-in staged render fixture.");
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "drawing-job.json")));
        var source = saved.RootElement.GetProperty("input").GetProperty("Request").Deserialize<ImageGenerationRequest>()!;
        var request = source with { SessionDirectory = ImageGenerationSessionStore.Create(_root), OutputFolder = Path.Combine(fixture, "verified-export") };
        File.Copy(Path.Combine(fixture, "cyberpunk-smiley.png"), Path.Combine(request.SessionDirectory, request.Id + "_0.pending.png"));
        File.Copy(Path.Combine(fixture, "legacy-runtime.json"), Path.Combine(request.SessionDirectory, request.Id + "_0.runtime.json"));
        var worker = new Worker { FailAt = 0 };

        var turn = await new ImageGenerationRuntime(worker).RunAsync(request, [], null, default);

        Assert.AreEqual(0, worker.Calls);
        Assert.IsTrue(turn.Results.Single().Exported);
        AssertSenseNovaMetadata(request);
    }

    private static void AssertSenseNovaMetadata(ImageGenerationRequest request)
    {
        var fields = PngTextMetadata.Read(ImageGenerationSessionStore.ResultPath(request, 0));
        Assert.AreEqual(request.Prompt, fields["Prompt"]);
        StringAssert.Contains(fields["LOPATA_PRODUCER"], VisualSenseNova.Backend);
        Assert.IsFalse(fields.ContainsKey("LOPATA_ENGINE_SET"));
        using var document = JsonDocument.Parse(fields["LOPATA"]);
        var metadata = document.RootElement;
        Assert.AreEqual(VisualSenseNova.Backend, metadata.GetProperty("Backend").GetString());
        Assert.AreEqual(JsonValueKind.Null, metadata.GetProperty("EngineSetId").ValueKind);
        Assert.AreEqual(VisualSenseNova.Revision, metadata.GetProperty("ModelRevision").GetString());
        Assert.AreEqual(VisualSenseNova.Profile, metadata.GetProperty("Runtime").GetProperty("ComponentId").GetString());
        Assert.AreEqual("8-step V2", metadata.GetProperty("Runtime").GetProperty("Schedule").GetString());
        Assert.AreEqual(VisualSenseNova.Id, metadata.GetProperty("Components")[0].GetProperty("Id").GetString());
        Assert.AreEqual(VisualSenseNova.Artifact.Files.Length, metadata.GetProperty("Components")[0].GetProperty("Files").GetArrayLength());
    }

    [TestMethod]
    public async Task RuntimeEmbedsUnicodeWindowsPropertiesAndActualParametersWithoutChangingPixels()
    {
        var request = Request("krea"); var worker = new Worker();
        var turn = await new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None);
        foreach (var result in turn.Results)
        {
            Assert.IsTrue(result.Exported);
            var canonical = ImageGenerationSessionStore.ResultPath(request, result.Index);
            var actual = File.ReadAllBytes(canonical);
            CollectionAssert.AreEqual(actual, File.ReadAllBytes(result.ExportPath!));
            CollectionAssert.AreEqual(Chunks(worker.Bytes[result.Index], "IDAT").SelectMany(b => b).ToArray(), Chunks(actual, "IDAT").SelectMany(b => b).ToArray());
            Assert.IsTrue(ImageGenerationRuntime.IsValidImage(canonical, request));
            using var input = File.OpenRead(canonical);
            var metadata = (BitmapMetadata)BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0].Metadata;
            Assert.AreEqual("Тестовый автор 🐈", metadata.Author.Single());
            Assert.AreEqual("Krea 2 Turbo #" + (7 + result.Index), metadata.Title);
            // WIC trims outer whitespace for display; the raw iTXt/JSON prompt below remains exact.
            Assert.AreEqual(request.Prompt.Trim(), metadata.Comment);
            Assert.AreEqual("LOPATA", metadata.ApplicationName);
            StringAssert.Contains(PngTextMetadata.Read(canonical)["LOPATA_PRODUCER"], "engine set unknown");
            Assert.IsFalse(string.IsNullOrWhiteSpace(metadata.DateTaken));
            CollectionAssert.AreEqual(new[] { "LOPATA", "AI", "Krea 2 Turbo" }, metadata.Keywords.ToArray());
            var text = TextFields(actual);
            Assert.AreEqual(request.Prompt, text["Description"]);
            using var document = JsonDocument.Parse(text["LOPATA"]);
            var json = document.RootElement; var model = ImageGenerationCatalog.Get(request.ModelId);
            Assert.AreEqual(request.Seeds[result.Index], json.GetProperty("Seed").GetInt64());
            Assert.AreEqual(model.Steps, json.GetProperty("Steps").GetInt32());
            Assert.AreEqual(model.Cfg, json.GetProperty("Cfg").GetDouble());
            Assert.AreEqual(model.Sampler, json.GetProperty("Sampler").GetString());
            Assert.AreEqual(1.15, json.GetProperty("FlowShift").GetDouble());
            Assert.AreEqual(request.SubmittedAt, json.GetProperty("SubmittedAt").GetDateTimeOffset());
            Assert.IsTrue(json.GetProperty("CreatedAt").GetDateTimeOffset() >= DateTimeOffset.Now.AddMinutes(-1));
            Assert.AreEqual(request.Prompt, json.GetProperty("Prompt").GetString());
            Assert.IsTrue(text["parameters"].StartsWith(request.Prompt + "\n", StringComparison.Ordinal));
            Assert.IsTrue(text["parameters"].Contains("Flow shift: 1.15", StringComparison.Ordinal));
            Assert.AreEqual("LOPATA; AI; Krea 2 Turbo", text["Keywords"]);
            var fixture = Environment.GetEnvironmentVariable("LOPATA_METADATA_FIXTURE");
            if (!string.IsNullOrWhiteSpace(fixture) && result.Index == 0) { Directory.CreateDirectory(Path.GetDirectoryName(fixture)!); File.Copy(canonical, fixture, true); }
        }
    }

    [TestMethod]
    [DataRow("qwen-image-21-base-gguf", ImageGenerationFileFormat.Png, false, "PNG", ".png")]
    [DataRow("qwen-image-21-turbo-int8", ImageGenerationFileFormat.Jpeg, false, "JPEG", ".jpg")]
    [DataRow("qwen-image-21-turbo-int8", ImageGenerationFileFormat.Jpeg, true, "JPEG", ".jpg")]
    [DataRow("qwen-image-21-turbo-int8", ImageGenerationFileFormat.WebP, false, "WEBP", ".webp")]
    [DataRow("z-image", ImageGenerationFileFormat.Png, false, "PNG", ".png")]
    [DataRow("krea", ImageGenerationFileFormat.Png, false, "PNG", ".png")]
    public async Task ReadersFindGenerationFieldsInEachExportFormat(string modelId, ImageGenerationFileFormat format,
        bool longPrompt, string fileType, string extension)
    {
        var request = Request(modelId) with
        {
            Prompt = longPrompt ? new string('я', 28000) + " Конец 🎨" : "Замок у моря 🎨",
            OutputFormat = format, Seeds = [123],
            Metadata = new("Тестовый автор 🐈", "image-generation/visual-scenario")
        };
        var model = ImageGenerationCatalog.Get(modelId);
        var revision = "actual-rendered-weights";
        var worker = new Worker
        {
            Transparent = true,
            Receipt = new("002", "runtime-version", "executed-commit", "vulkan", "runtime.sd.vulkan")
            { ArtistRevision = revision, Schedule = model.Sigmas }
        };
        var runtime = new ImageGenerationRuntime(worker);
        var turn = await runtime.RunAsync(request, [], null, CancellationToken.None);
        var result = turn.Results.Single();
        Assert.IsTrue(result.Exported, result.ProcessingError ?? result.ExportError);
        var output = result.ExportPath!;
        Assert.AreEqual(extension, Path.GetExtension(output));
        var original = ImageGenerationSessionStore.ResultPath(request, 0);
        CollectionAssert.AreEqual(Chunks(worker.Bytes[0], "IDAT").SelectMany(bytes => bytes).ToArray(),
            Chunks(File.ReadAllBytes(original), "IDAT").SelectMany(bytes => bytes).ToArray());
        using var decoded = SKBitmap.Decode(output);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(request.Width, decoded.Width);
        Assert.AreEqual(request.Height, decoded.Height);
        var corner = decoded.GetPixel(0, 0);
        if (format == ImageGenerationFileFormat.Jpeg)
        {
            Assert.AreEqual(byte.MaxValue, corner.Alpha);
            Assert.IsGreaterThan((byte)249, corner.Red);
            Assert.IsGreaterThan((byte)249, corner.Green);
            Assert.IsGreaterThan((byte)249, corner.Blue);
        }
        else Assert.AreEqual((byte)0, corner.Alpha);

        var oldGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            var raw = await FileMetadataExifTool.ReadAsync(output, default);
            var type = FileMetadataPolicy.RawValue(raw, "File:FileType").ToUpperInvariant();
            if (format == ImageGenerationFileFormat.WebP)
            {
                StringAssert.Contains(type, "WEBP");
                StringAssert.Contains(type, "(LOSSLESS)");
                Assert.AreEqual("image/webp", FileMetadataPolicy.RawValue(raw, "File:MIMEType"));
                var snapshot = await new FileMetadataReader().ReadAsync(output, default);
                Assert.IsNull(snapshot.Error);
                Assert.AreEqual("WEBP", snapshot.Format);
            }
            else Assert.AreEqual(fileType, type);
            Assert.AreEqual("LOPATA", FileMetadataPolicy.RawValue(raw, "XMP-xmp:CreatorTool"));
            Assert.AreEqual(request.Metadata.Author, FileMetadataPolicy.RawValue(raw, "XMP-dc:Creator"));
            Assert.AreEqual(request.Prompt, FileMetadataPolicy.RawValue(raw, "XMP-dc:Description"));
            Assert.AreEqual(request.Prompt, FileMetadataPolicy.RawValue(raw, "XMP-lopata:Prompt"));
            if (format == ImageGenerationFileFormat.Png)
                Assert.AreEqual(request.Prompt, PngTextMetadata.Read(output)["Prompt"]);
            Assert.AreEqual("123", FileMetadataPolicy.RawValue(raw, "XMP-lopata:Seed"));
            Assert.AreEqual(revision, FileMetadataPolicy.RawValue(raw, "XMP-lopata:ModelRevision"));
            using var generation = JsonDocument.Parse(FileMetadataPolicy.RawValue(raw, "XMP-lopata:Generation"));
            Assert.AreEqual(fileType, generation.RootElement.GetProperty("OutputFormat").GetString());
            Assert.AreEqual(request.Prompt, generation.RootElement.GetProperty("Prompt").GetString());
            Assert.AreEqual("executed-commit", generation.RootElement.GetProperty("BackendCommit").GetString());
            Assert.IsFalse(generation.RootElement.GetRawText().Contains(request.SessionDirectory, StringComparison.Ordinal));
        }
        finally { ComponentLicenseGate.ConfirmAsync = oldGate; }

        var sizeBefore = new FileInfo(output).Length;
        await runtime.RunAsync(request, [], null, CancellationToken.None);
        Assert.AreEqual(1, worker.Calls);
        CollectionAssert.AreEqual(new[] { output }, Directory.GetFiles(request.OutputFolder));
        Assert.AreEqual(sizeBefore, new FileInfo(output).Length);
    }

    [TestMethod]
    public void FallbackAndInvariantParametersDoNotLeakOtherProfileDataOrPaths()
    {
        Assert.AreEqual("LOPATA User", ImageGenerationMetadata.Author(null));
        Assert.AreEqual("LOPATA User", ImageGenerationMetadata.Author(" \t "));
        Assert.AreEqual("Ник", ImageGenerationMetadata.Author(" Ник "));
        var request = Request() with { Metadata = new(" "), ModelsRoot = "PRIVATE-MODEL-DIRECTORY" };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var fields = ImageGenerationMetadata.Create(request, 0, DateTimeOffset.Now);
            Assert.AreEqual("LOPATA User", fields["Author"]);
            Assert.IsFalse(fields.Values.Any(v => v.Contains(request.ModelsRoot, StringComparison.Ordinal) || v.Contains(request.SessionDirectory, StringComparison.Ordinal)));
            using var document = JsonDocument.Parse(fields["LOPATA"]);
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("FlowShift").ValueKind);
            Assert.AreEqual("LOPATA", document.RootElement.GetProperty("Program").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("ProgramVersion").GetString()));
            Assert.IsFalse(fields.ContainsKey("Copyright")); Assert.IsFalse(fields.ContainsKey("Rating"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public async Task PausedSerializedRequestKeepsAuthorAndCompletedReplayKeepsExactFiles()
    {
        var request = Request(); var worker = new Worker { FailAt = 1 }; var runtime = new ImageGenerationRuntime(worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.RunAsync(request, [], null, CancellationToken.None));
        var first = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single().Results.Single();
        var before = File.ReadAllBytes(first.ExportPath!);
        var restored = JsonSerializer.Deserialize<ImageGenerationRequest>(JsonSerializer.Serialize(request))!;
        worker.FailAt = -1;
        await runtime.RunAsync(restored, [], null, CancellationToken.None);
        var calls = worker.Calls;
        await runtime.RunAsync(restored, [], null, CancellationToken.None);
        Assert.AreEqual(calls, worker.Calls); Assert.HasCount(2, Directory.GetFiles(request.OutputFolder));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(first.ExportPath!));
        Assert.AreEqual("Тестовый автор 🐈", TextFields(File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 1)))["Author"]);
        // Recover the published PNG if publication preceded the result checkpoint.
        var session = ImageGenerationSessionStore.Load(request.SessionDirectory);
        ImageGenerationSessionStore.Save(request.SessionDirectory, session with { Turns = [session.Turns.Single() with { Results = [] }] });
        await runtime.RunAsync(restored, [], null, CancellationToken.None);
        Assert.AreEqual(calls, worker.Calls);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0)));
    }

    [TestMethod]
    public async Task LegacyRequestIsReadableAndDoesNotRewriteExistingPng()
    {
        var request = Request() with { Metadata = null, Seeds = [123] }; var worker = new Worker();
        var runtime = new ImageGenerationRuntime(worker);
        await runtime.RunAsync(request, [], null, CancellationToken.None);
        var original = File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0));
        CollectionAssert.AreEqual(worker.Bytes[0], original);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var oldJson = JsonSerializer.Serialize(document.RootElement.EnumerateObject().Where(p => p.Name != "Metadata").ToDictionary(p => p.Name, p => p.Value));
        var legacy = JsonSerializer.Deserialize<ImageGenerationRequest>(oldJson)!;
        Assert.IsNull(legacy.Metadata);
        await runtime.RunAsync(legacy, [], null, CancellationToken.None);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0)));
    }

    [TestMethod]
    public async Task RepeatedMetadataWriteReplacesOwnedFieldsAndPreservesUnrelatedChunks()
    {
        var request = Request(); var worker = new Worker();
        await new ImageGenerationRuntime(worker).RunAsync(request with { Metadata = null, Seeds = [123] }, [], null, CancellationToken.None);
        var path = ImageGenerationSessionStore.ResultPath(request, 0);
        PngMetadataWriter.Write(path, new Dictionary<string, string> { ["Backend note"] = "keep unchanged" });
        var before = File.ReadAllBytes(path);
        var fields = ImageGenerationMetadata.Create(request, 0, request.SubmittedAt!.Value);
        PngMetadataWriter.Write(path, fields); var once = File.ReadAllBytes(path);
        PngMetadataWriter.Write(path, fields); var twice = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(once, twice);
        CollectionAssert.AreEqual(Chunks(before, "IDAT").SelectMany(b => b).ToArray(), Chunks(twice, "IDAT").SelectMany(b => b).ToArray());
        var text = TextFields(twice);
        Assert.AreEqual("keep unchanged", text["Backend note"]);
        Assert.AreEqual(fields.Count + 1, text.Count);
    }

    [TestMethod]
    public async Task CorruptOrCanceledWriteLeavesOriginalIntactAndRemovesOwnTemporaryFile()
    {
        var request = Request(); var worker = new Worker();
        await new ImageGenerationRuntime(worker).RunAsync(request with { Metadata = null, Seeds = [123] }, [], null, CancellationToken.None);
        var path = ImageGenerationSessionStore.ResultPath(request, 0); var original = File.ReadAllBytes(path);
        var fields = ImageGenerationMetadata.Create(request, 0, DateTimeOffset.Now);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PngMetadataWriter.Write(path, fields, canceled.Token));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        var corrupt = original.ToArray(); corrupt[^1] ^= 1; File.WriteAllBytes(path, corrupt);
        Assert.Throws<InvalidDataException>(() => PngMetadataWriter.Write(path, fields));
        CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(path));
        Assert.HasCount(0, Directory.GetFiles(request.SessionDirectory, "*.metadata.tmp"));
    }

    private static List<byte[]> Chunks(byte[] png, string requested)
    {
        var chunks = new List<byte[]>();
        for (var offset = 8; offset < png.Length;)
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == requested) chunks.Add(png.AsSpan(offset + 8, count).ToArray());
            offset += count + 12;
        }
        return chunks;
    }
    private static Dictionary<string, string> TextFields(byte[] png) => Chunks(png, "iTXt").ToDictionary(data =>
        Encoding.Latin1.GetString(data, 0, Array.IndexOf(data, (byte)0)), data =>
        {
            var start = Array.IndexOf(data, (byte)0) + 3; // Skip compression flag and method.
            start = Array.IndexOf(data, (byte)0, start) + 1; // Language.
            start = Array.IndexOf(data, (byte)0, start) + 1; // Translated keyword.
            return Encoding.UTF8.GetString(data, start, data.Length - start);
        }, StringComparer.Ordinal);
    private sealed class Worker : IImageGenerationWorker
    {
        public Dictionary<int, byte[]> Bytes { get; } = [];
        public int Calls { get; private set; }
        public int FailAt { get; set; } = -1;
        public bool Transparent { get; init; }
        public ImageGenerationRuntimeReceipt? Receipt { get; init; }
        public async Task<ImageGenerationRuntimeReceipt?> GenerateWithReceiptAsync(ImageGenerationRequest request, int index,
            IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            await GenerateAsync(request, index, cards, promptFile, output, token);
            return Receipt;
        }
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            Calls++; if (index == FailAt) throw new OperationCanceledException();
            using var bitmap = new SKBitmap(request.Width, request.Height);
            bitmap.Erase(Transparent ? SKColors.Transparent : SKColors.Coral);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            Bytes[index] = data.ToArray(); File.WriteAllBytes(output, Bytes[index]); return Task.CompletedTask;
        }
    }
}
