using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
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
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            Calls++; if (index == FailAt) throw new OperationCanceledException();
            using var bitmap = new SKBitmap(request.Width, request.Height); bitmap.Erase(SKColors.Coral);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            Bytes[index] = data.ToArray(); File.WriteAllBytes(output, Bytes[index]); return Task.CompletedTask;
        }
    }
}
