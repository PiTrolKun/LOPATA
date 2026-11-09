using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Lopata.Collection;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageUtilitySourceHistoryTests
{
    private static readonly OutputModelUse Prior = new("source-generator", "Historical source model", "pinned-source-revision", "image generation");

    [TestMethod]
    [DataRow("png", "qoi", "xmp", false)]
    [DataRow("png", "png", "png", false)]
    [DataRow("qoi", "png", "ads", false)]
    [DataRow("qoi", "png", "ads", true)]
    public async Task PublishedSourceModelsSurviveConversionAndCurrentAiIsAdded(string inputFormat, string outputFormat, string metadata, bool ai)
    {
        using var f = new Fixture(); var source = await f.Source(inputFormat);
        var facts = new OutputProvenance { Scenario = "image-generation/source", Models = [Prior, Prior], ModelHistory = "recorded" };
        if (metadata == "png") PngMetadataWriter.Write(source, new Dictionary<string, string> { ["LOPATA"] = facts.Json() });
        if (metadata == "xmp")
        {
            var xmp = Path.Combine(f.Root, "source.xmp"); File.WriteAllText(xmp, facts.Xmp());
            var tagged = Path.Combine(f.Root, "tagged.png"); await f.Run(source, "-profile", xmp, tagged); source = tagged;
        }
        if (metadata == "ads") Assert.IsTrue(CollectionAlternateOrigin.Write(source, facts.Json(), Fixture.Hash(source)));
        var before = Fixture.Hash(source); var original = source;
        var executor = new ControlledAi(ai ? () =>
        {
            // Simulate an external edit after the processor captured its used input snapshot.
            var bytes = File.ReadAllBytes(original); bytes[14] ^= 1; File.WriteAllBytes(original, bytes);
        } : null);
        var output = await new ImageUtilityProcessor(f.Magick, executor).ProcessAsync(new() { Source = source, DisplayName = Path.GetFileName(source) },
            new() { FormatOnly = !ai, MethodId = "real-esrgan", Preset = 360, Format = outputFormat }, Path.Combine(f.Root, "output"));
        var result = Fixture.Facts(output);
        Assert.AreEqual(1, result.Models.Count(m => m == Prior), "A known revision was lost or duplicated.");
        Assert.AreEqual(ai ? 2 : 1, result.Models.Length);
        Assert.AreEqual(ai ? 1 : 0, executor.Calls);
        if (ai) Assert.IsTrue(result.Models.Any(m => m.Id == "real-esrgan/realesrgan-x4plus"));
        Assert.Contains("source-metadata-and-current-operation", result.ModelHistory);
        Assert.Contains("earlier-history-unknown", result.ModelHistory);
        if (!ai) Assert.AreEqual(before, Fixture.Hash(source));
    }

    [TestMethod]
    public async Task LegacyGenerationPngKeepsPinnedWeightRevisionWithoutCopyingItsPrompt()
    {
        using var f = new Fixture(); var source = await f.Source("png");
        var json = JsonSerializer.Serialize(new { Schema = 1, Program = "LOPATA", ModelId = "old-model", Model = "Old model",
            Prompt = "Do not transfer this prompt into new public facts", Components = new[] { new { Id = "old-weights", Repository = "public/repository", Revision = "actual-pinned-weights" } },
            AppliedLoras = new[] { new { Id = "used-lora", Repository = "public/lora", Revision = "actual-pinned-lora", Trigger = "Do not transfer this private trigger" } } });
        PngMetadataWriter.Write(source, new Dictionary<string, string> { ["LOPATA"] = json });
        var output = await new ImageUtilityProcessor(f.Magick).ProcessAsync(new() { Source = source, DisplayName = "source.png" },
            new() { FormatOnly = true, Format = "qoi" }, Path.Combine(f.Root, "output"));
        var result = Fixture.Facts(output);
        Assert.IsTrue(result.Models.Any(m => m.Id == "old-weights" && m.Revision == "actual-pinned-weights"));
        Assert.IsTrue(result.Models.Any(m => m.Id == "old-model"));
        Assert.IsTrue(result.Models.Any(m => m.Id == "used-lora" && m.Revision == "actual-pinned-lora" && m.Role == "source image generation LoRA"));
        Assert.IsFalse(result.Json().Contains("Do not transfer this prompt", StringComparison.Ordinal));
        Assert.IsFalse(result.Json().Contains("Do not transfer this private trigger", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InvalidPrivateModelFieldsAreExcludedWhileValidHistoryRemains()
    {
        using var f = new Fixture(); var source = await f.Source("png");
        var json = JsonSerializer.Serialize(new { Schema = 1, Program = "LOPATA", Models = new[] { Prior,
            new OutputModelUse("private", @"C:\secret\model.gguf", "r", "generation"),
            new OutputModelUse("control", "Model", "r", "a\nb") } });
        PngMetadataWriter.Write(source, new Dictionary<string, string> { ["LOPATA"] = json });
        var output = await new ImageUtilityProcessor(f.Magick).ProcessAsync(new() { Source = source, DisplayName = "source.png" },
            new() { FormatOnly = true }, Path.Combine(f.Root, "output"));
        var result = Fixture.Facts(output); CollectionAssert.AreEqual(new[] { Prior }, result.Models);
        Assert.Contains("source-history-incomplete", result.ModelHistory);
        Assert.IsFalse(result.Json().Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASourceStreamBoundToOtherBytesCannotSupplyModelHistory()
    {
        using var f = new Fixture(); var source = await f.Source("qoi");
        Assert.IsTrue(CollectionAlternateOrigin.Write(source, new OutputProvenance { Models = [Prior] }.Json(), new string('f', 64)));
        var output = await new ImageUtilityProcessor(f.Magick).ProcessAsync(new() { Source = source, DisplayName = "source.qoi" },
            new() { FormatOnly = true }, Path.Combine(f.Root, "output"));
        var result = Fixture.Facts(output); Assert.IsEmpty(result.Models); Assert.Contains("source-history-incomplete", result.ModelHistory);
    }

    private sealed class ControlledAi(Action? duringCall) : IImageUtilityAiProcessor
    {
        internal int Calls;
        public Task ProcessAsync(string methodId, string inputPath, string outputPngPath, IReadOnlyDictionary<string, string> parameters,
            IProgress<ImageUtilityProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; duringCall?.Invoke(); File.Copy(inputPath, outputPngPath); return Task.CompletedTask; }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LOPATA-ImageHistory-tests", Guid.NewGuid().ToString("N"));
        internal string Magick => Path.Combine(AppContext.BaseDirectory, "ImageUtilityRuntime", "ImageMagick", "magick.exe");
        private readonly string? _previous = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        internal Fixture() { Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", Path.Combine(Root, "journal")); }
        internal async Task<string> Source(string format)
        {
            var png = Path.Combine(Root, "source.png"); await Run("-size", "64x48", "gradient:red-blue", "-depth", "8", png);
            if (format == "png") return png;
            var source = Path.Combine(Root, "source." + format); await Run(png, format + ":" + source); return source;
        }
        internal async Task Run(params string[] args) => _ = await FileMetadataProcess.RunAsync(Magick, args, default);
        internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        internal static OutputProvenance Facts(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path + ":" + CollectionAlternateOrigin.StreamName));
            return JsonSerializer.Deserialize<OutputProvenance>(json.RootElement.GetProperty("provenance").GetRawText())!;
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT", _previous);
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-ImageHistory-tests")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(expected, Path.GetFullPath(Root)); Directory.Delete(Root, true);
        }
    }
}
